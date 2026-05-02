using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

public class RagEngine
{
    private readonly EmbeddingService _embed;
    private readonly VectorStore _store;
    private readonly HttpClient _http;
    private readonly RagOptions _options;

    public RagEngine(EmbeddingService embed, VectorStore store, HttpClient http, IOptions<RagOptions> options)
    {
        _embed = embed;
        _store = store;
        _http = http;
        _options = options.Value;
    }

    public async Task<AskResult> AskWithKnowledgeBaseAsync(
        string query,
        string generationModel,
        IReadOnlyList<ChatTurn>? history = null,
        CancellationToken ct = default)
    {
        var prepared = await PrepareWithKnowledgeBaseAsync(query, history, ct);
        if (prepared.ShortCircuitResult is not null)
        {
            return prepared.ShortCircuitResult;
        }

        var answer = await GenerateAsync(prepared.Prompt, generationModel, ct);
        return new AskResult(answer, prepared.Citations);
    }

    public async Task<AskPreparation> PrepareWithKnowledgeBaseAsync(
        string query,
        IReadOnlyList<ChatTurn>? history = null,
        CancellationToken ct = default)
    {
        var normalizedQuery = EmbeddingService.NormalizeForEmbedding(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            throw new OllamaResponseException("Query is empty after normalization.");
        }

        var queryEmbedding = await _embed.EmbedAsync(normalizedQuery, ct);
        var docs = _store.Query(queryEmbedding, _options.TopK);
        if (docs.Count == 0)
        {
            return new AskPreparation(
                Prompt: string.Empty,
                Citations: Array.Empty<Citation>(),
                ShortCircuitResult: new AskResult(
                "I couldn't find relevant information in the knowledge base.",
                Array.Empty<Citation>()));
        }

        var context = BuildBoundedContext(docs, _options.MaxContextChars);

        var historySection = BuildHistorySection(history);
        var prompt = $@"
You are a precise assistant answering questions using ONLY the provided context.

Rules:
- If the answer is not in the context, say: ""I don't know based on the provided context.""
- Do NOT make up information
- Prefer concise, accurate answers
- Quote relevant parts when useful

Conversation History:
{historySection}

Context:
{context}

Current Question: {query}

Answer:
";

        var citations = docs
            .Select(d => new Citation(Path.GetFileName(d.Source), d.ChunkIndex))
            .Distinct()
            .ToArray();

        return new AskPreparation(prompt, citations);
    }

    public async Task<AskResult> AskDirectAsync(
        string query,
        string generationModel,
        IReadOnlyList<ChatTurn>? history = null,
        CancellationToken ct = default)
    {
        var prepared = PrepareDirect(query, history);
        var answer = await GenerateAsync(prepared.Prompt, generationModel, ct);
        return new AskResult(answer, prepared.Citations);
    }

    public AskPreparation PrepareDirect(
        string query,
        IReadOnlyList<ChatTurn>? history = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new OllamaResponseException("Query is required.");
        }

        var historySection = BuildHistorySection(history);
        var prompt = $@"
You are a concise and accurate assistant.
Answer the user's question directly. If you are unsure, say so clearly.

Conversation History:
{historySection}

Current Question: {query}

Answer:
";

        return new AskPreparation(prompt, Array.Empty<Citation>());
    }

    private static string BuildBoundedContext(IReadOnlyList<DocumentRecord> docs, int maxContextChars)
    {
        if (maxContextChars <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var doc in docs)
        {
            var segment = $"[Source: {Path.GetFileName(doc.Source)} | Chunk: {doc.ChunkIndex}]\n{doc.Text}";
            var withSeparator = sb.Length == 0 ? segment : $"\n\n---\n\n{segment}";
            if (sb.Length + withSeparator.Length > maxContextChars)
            {
                break;
            }

            sb.Append(withSeparator);
        }

        return sb.ToString();
    }

    private class GenResponse
    {
        public string Response { get; set; } = "";
    }

    private static string BuildHistorySection(IReadOnlyList<ChatTurn>? history)
    {
        if (history is null || history.Count == 0)
        {
            return "(none)";
        }

        var lines = history
            .Where(turn => turn is not null)
            .Select(turn =>
            {
                var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "Assistant" : "User";
                var content = turn.Content?.Trim();
                return string.IsNullOrWhiteSpace(content) ? null : $"{role}: {content}";
            })
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return lines.Length == 0 ? "(none)" : string.Join('\n', lines);
    }

    private sealed class StreamChunk
    {
        public string? Response { get; set; }
        public bool Done { get; set; }
        public string? Error { get; set; }
    }

    public async IAsyncEnumerable<string> StreamGenerateAsync(
        string prompt,
        string generationModel,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.OllamaBaseUrl.TrimEnd('/')}/api/generate")
        {
            Content = JsonContent.Create(new
            {
                model = generationModel,
                prompt,
                stream = true
            })
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OllamaTimeoutException("Generation request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaRequestException("Generation service is unavailable.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new OllamaRequestException($"Generation API error {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            StreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<StreamChunk>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                throw new OllamaResponseException("Failed to parse generation stream response.", ex);
            }

            if (chunk is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(chunk.Error))
            {
                throw new OllamaResponseException(chunk.Error);
            }

            if (!string.IsNullOrWhiteSpace(chunk.Response))
            {
                yield return chunk.Response;
            }

            if (chunk.Done)
            {
                break;
            }
        }
    }

    public record AskPreparation(string Prompt, IReadOnlyList<Citation> Citations, AskResult? ShortCircuitResult = null);

    private async Task<string> GenerateAsync(string prompt, string generationModel, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync($"{_options.OllamaBaseUrl.TrimEnd('/')}/api/generate", new
            {
                model = generationModel,
                prompt,
                stream = false
            }, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OllamaTimeoutException("Generation request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaRequestException("Generation service is unavailable.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new OllamaRequestException($"Generation API error {(int)response.StatusCode}.");
        }

        GenResponse? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<GenResponse>(cancellationToken: ct);
        }
        catch (NotSupportedException ex)
        {
            throw new OllamaResponseException("Generation response had unsupported content type.", ex);
        }
        catch (JsonException ex)
        {
            throw new OllamaResponseException("Failed to parse generation response.", ex);
        }

        if (result == null || string.IsNullOrWhiteSpace(result.Response))
        {
            throw new OllamaResponseException("Generation response was empty.");
        }

        return result.Response;
    }
}

public record Citation(string Source, int ChunkIndex);
public record AskResult(string Answer, IReadOnlyList<Citation> Citations);
