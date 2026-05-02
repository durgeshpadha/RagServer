using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RagServer.Web.Models;

namespace RagServer.Web.Services;

public sealed class RagApiClient
{
    private readonly HttpClient _httpClient;

    public RagApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AskResponse> AskAsync(
        string query,
        string? model,
        bool useKnowledgeBase,
        IReadOnlyList<ChatTurn>? history,
        CancellationToken ct)
    {
        var response = await _httpClient.PostAsJsonAsync("ask", new AskRequest(query, model, useKnowledgeBase, history), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, ct);
        }

        var content = await response.Content.ReadFromJsonAsync<JsonElementWrapper>(cancellationToken: ct);
        if (content is null)
        {
            throw new InvalidOperationException("API returned an empty ask response.");
        }

        var citations = content.Citations?.Select(c => new Citation(c.Source ?? "unknown", c.ChunkIndex)).ToList()
            ?? new List<Citation>();

        return new AskResponse(content.Answer ?? string.Empty, citations);
    }

    public async IAsyncEnumerable<AskStreamEvent> StreamAskAsync(
        string query,
        string? model,
        bool useKnowledgeBase,
        IReadOnlyList<ChatTurn>? history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "ask/stream")
        {
            Content = JsonContent.Create(new AskRequest(query, model, useKnowledgeBase, history))
        };

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, ct);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var eventName = string.Empty;
        var dataBuilder = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataBuilder.Append(line["data:".Length..].Trim());
                continue;
            }

            if (line.Length == 0 && dataBuilder.Length > 0)
            {
                var payload = dataBuilder.ToString();
                dataBuilder.Clear();

                if (string.Equals(eventName, "token", StringComparison.OrdinalIgnoreCase))
                {
                    var token = JsonSerializer.Deserialize<AskStreamTokenEvent>(payload);
                    if (token is not null)
                    {
                        yield return new AskStreamEvent("token", Token: token);
                    }
                }
                else if (string.Equals(eventName, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    var completed = JsonSerializer.Deserialize<AskStreamCompletedEvent>(payload);
                    if (completed is not null)
                    {
                        yield return new AskStreamEvent("completed", Completed: completed);
                    }
                }
                else if (string.Equals(eventName, "error", StringComparison.OrdinalIgnoreCase))
                {
                    var error = JsonSerializer.Deserialize<AskStreamErrorEvent>(payload);
                    if (error is not null)
                    {
                        yield return new AskStreamEvent("error", Error: error);
                    }
                }

                eventName = string.Empty;
            }
        }
    }

    public async Task<ModelsResponse> GetModelsAsync(CancellationToken ct)
    {
        var response = await _httpClient.GetAsync("models", ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, ct);
        }

        var payload = await response.Content.ReadFromJsonAsync<ModelsResponse>(cancellationToken: ct);
        if (payload is null)
        {
            throw new InvalidOperationException("API returned an empty models response.");
        }

        return payload;
    }

    public async Task<IngestResponse> IngestAsync(CancellationToken ct)
    {
        var response = await _httpClient.PostAsync("ingest", content: null, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, ct);
        }

        var result = await response.Content.ReadFromJsonAsync<IngestResponse>(cancellationToken: ct);
        return result ?? throw new InvalidOperationException("API returned an empty ingest response.");
    }

    public async IAsyncEnumerable<IngestProgressEvent> StreamIngestAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "ingest/stream");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, ct);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var dataBuilder = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataBuilder.Append(line["data:".Length..].Trim());
                continue;
            }

            if (line.Length == 0 && dataBuilder.Length > 0)
            {
                var payload = JsonSerializer.Deserialize<IngestProgressEvent>(dataBuilder.ToString());
                dataBuilder.Clear();

                if (payload is not null)
                {
                    yield return payload;
                }
            }
        }
    }

    public async Task<int> GetDataCountAsync(CancellationToken ct)
    {
        var response = await _httpClient.GetAsync("data/count", ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, ct);
        }

        var payload = await response.Content.ReadFromJsonAsync<DataCountResponse>(cancellationToken: ct);
        return payload?.TotalStored ?? 0;
    }

    public async Task<ClearDataResponse> ClearDataAsync(CancellationToken ct)
    {
        var response = await _httpClient.DeleteAsync("data", ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateApiExceptionAsync(response, ct);
        }

        var payload = await response.Content.ReadFromJsonAsync<ClearDataResponse>(cancellationToken: ct);
        return payload ?? new ClearDataResponse("RAG data cleared.", 0);
    }

    private static async Task<Exception> CreateApiExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken: ct);
            if (error is not null && !string.IsNullOrWhiteSpace(error.Message))
            {
                return new InvalidOperationException($"{(int)response.StatusCode} {response.StatusCode}: {error.Message}");
            }
        }
        catch
        {
            // Fall through to raw content fallback.
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        var message = string.IsNullOrWhiteSpace(content)
            ? $"API request failed with status {(int)response.StatusCode} ({response.StatusCode})."
            : $"API request failed with status {(int)response.StatusCode} ({response.StatusCode}): {content}";

        return new InvalidOperationException(message);
    }

    private sealed class JsonElementWrapper
    {
        public string? Answer { get; set; }
        public List<CitationPayload>? Citations { get; set; }
    }

    private sealed class CitationPayload
    {
        public string? Source { get; set; }
        public int ChunkIndex { get; set; }
    }
}
