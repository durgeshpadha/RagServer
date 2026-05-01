using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

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

    public async Task<AskResult> AskAsync(string query, CancellationToken ct = default)
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
            return new AskResult(
                "I couldn't find relevant information in the knowledge base.",
                Array.Empty<Citation>());
        }

        var context = BuildBoundedContext(docs, _options.MaxContextChars);

        var prompt = $@"
You are a precise assistant answering questions using ONLY the provided context.

Rules:
- If the answer is not in the context, say: ""I don't know based on the provided context.""
- Do NOT make up information
- Prefer concise, accurate answers
- Quote relevant parts when useful

Context:
{context}

Question: {query}

Answer:
";

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync($"{_options.OllamaBaseUrl.TrimEnd('/')}/api/generate", new
            {
                model = _options.GenerationModel,
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

        var citations = docs
            .Select(d => new Citation(Path.GetFileName(d.Source), d.ChunkIndex))
            .Distinct()
            .ToArray();

        return new AskResult(result.Response, citations);
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
}

public record Citation(string Source, int ChunkIndex);
public record AskResult(string Answer, IReadOnlyList<Citation> Citations);
