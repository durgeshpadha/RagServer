using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

public class EmbeddingService
{
    private readonly HttpClient _http;
    private readonly RagOptions _options;

    public EmbeddingService(HttpClient http, IOptions<RagOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var normalized = NormalizeForEmbedding(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new OllamaResponseException("Embedding input was empty after normalization.");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync($"{_options.OllamaBaseUrl.TrimEnd('/')}/api/embed", new
            {
                model = _options.EmbeddingModel,
                input = normalized
            }, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OllamaTimeoutException("Embedding request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaRequestException("Embedding service is unavailable.", ex);
        }

        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new OllamaRequestException($"Embedding API error {(int)response.StatusCode}.");
        }

        EmbedResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<EmbedResponse>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            throw new OllamaResponseException("Failed to parse embedding response.", ex);
        }

        if (result == null)
        {
            throw new OllamaResponseException("Embedding response was empty.");
        }

        if (result.Embeddings != null && result.Embeddings.Count > 0)
            return result.Embeddings[0];

        if (result.Embedding != null && result.Embedding.Length > 0)
            return result.Embedding;

        throw new OllamaResponseException("Embedding response contained no vectors.");
    }

    public static string NormalizeForEmbedding(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        return text.Replace("\r\n", "\n").Trim();
    }

    private class EmbedResponse
    {
        public float[]? Embedding { get; set; }
        public List<float[]>? Embeddings { get; set; }
    }
}

public class OllamaRequestException : Exception
{
    public OllamaRequestException(string message) : base(message) { }
    public OllamaRequestException(string message, Exception inner) : base(message, inner) { }
}

public class OllamaTimeoutException : Exception
{
    public OllamaTimeoutException(string message) : base(message) { }
    public OllamaTimeoutException(string message, Exception inner) : base(message, inner) { }
}

public class OllamaResponseException : Exception
{
    public OllamaResponseException(string message) : base(message) { }
    public OllamaResponseException(string message, Exception inner) : base(message, inner) { }
}
