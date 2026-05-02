using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

public sealed class QdrantVectorStore : IVectorStore
{
    private readonly HttpClient _http;
    private readonly RagOptions _options;
    private readonly ILogger<QdrantVectorStore> _logger;
    private readonly Lock _collectionStateLock = new();
    private readonly SemaphoreSlim _ensureCollectionMutex = new(1, 1);
    private readonly Dictionary<string, int> _collectionVectorSizes = new(StringComparer.OrdinalIgnoreCase);

    public QdrantVectorStore(HttpClient http, IOptions<RagOptions> options, ILogger<QdrantVectorStore> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        ManagedCollections = BuildManagedCollections(_options);
    }

    public IReadOnlyList<string> ManagedCollections { get; }

    public string ResolveCollection(string rootPath, string sourcePath)
    {
        var bucket = CollectionBucketResolver.ResolveBucket(rootPath, sourcePath, _options.CollectionBuckets);
        return CollectionBucketResolver.BuildCollectionName(_options.QdrantCollectionPrefix, bucket);
    }

    public async Task DeleteBySourceAsync(string collection, string source, CancellationToken ct = default)
    {
        if (!await CollectionExistsAsync(collection, ct))
        {
            return;
        }

        var body = new
        {
            filter = new
            {
                must = new object[]
                {
                    new
                    {
                        key = "source",
                        match = new { value = source }
                    }
                }
            }
        };

        await SendAsync(
            HttpMethod.Post,
            $"/collections/{Uri.EscapeDataString(collection)}/points/delete?wait=true",
            body,
            ct,
            allowNotFound: true);
    }

    public async Task UpsertAsync(string collection, IReadOnlyList<DocumentRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0)
        {
            return;
        }

        var vectorSize = records[0].Embedding.Length;
        if (vectorSize <= 0)
        {
            throw new InvalidOperationException("Cannot upsert vectors with empty dimensions.");
        }

        await EnsureCollectionAsync(collection, vectorSize, ct);

        var points = records.Select(r => new
        {
            id = r.Id,
            vector = r.Embedding,
            payload = new
            {
                source = r.Source,
                chunkIndex = r.ChunkIndex,
                text = r.Text
            }
        });

        await SendAsync(
            HttpMethod.Put,
            $"/collections/{Uri.EscapeDataString(collection)}/points?wait=true",
            new { points },
            ct);
    }

    public async Task<IReadOnlyList<ScoredDocumentRecord>> QueryAsync(float[] queryEmbedding, int topK, CancellationToken ct = default)
    {
        if (topK <= 0 || queryEmbedding.Length == 0)
        {
            return Array.Empty<ScoredDocumentRecord>();
        }

        var results = new List<ScoredDocumentRecord>();

        foreach (var collection in ManagedCollections)
        {
            if (!await CollectionExistsAsync(collection, ct))
            {
                continue;
            }

            var body = new
            {
                vector = queryEmbedding,
                limit = topK,
                with_payload = true,
                with_vector = false
            };

            var response = await SendAsync<QdrantSearchResult[]>(
                HttpMethod.Post,
                $"/collections/{Uri.EscapeDataString(collection)}/points/search",
                body,
                ct,
                allowNotFound: true);

            if (response is null)
            {
                continue;
            }

            foreach (var hit in response)
            {
                if (hit.Payload is null)
                {
                    continue;
                }

                var id = hit.Id.ValueKind switch
                {
                    JsonValueKind.String => hit.Id.GetString() ?? Guid.NewGuid().ToString("D"),
                    JsonValueKind.Number => hit.Id.GetRawText(),
                    _ => Guid.NewGuid().ToString("D")
                };

                if (!TryBuildDocument(hit.Payload.Value, id, out var document))
                {
                    continue;
                }

                results.Add(new ScoredDocumentRecord(document, hit.Score, collection));
            }
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToArray();
    }

    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        long total = 0;
        foreach (var collection in ManagedCollections)
        {
            if (!await CollectionExistsAsync(collection, ct))
            {
                continue;
            }

            var response = await SendAsync<QdrantCountResult>(
                HttpMethod.Post,
                $"/collections/{Uri.EscapeDataString(collection)}/points/count",
                new { exact = true },
                ct,
                allowNotFound: true);

            if (response is not null)
            {
                total += response.Count;
            }
        }

        return total;
    }

    public async Task<long> ClearAsync(CancellationToken ct = default)
    {
        var existing = await CountAsync(ct);

        foreach (var collection in ManagedCollections)
        {
            if (!await CollectionExistsAsync(collection, ct))
            {
                continue;
            }

            await SendAsync(
                HttpMethod.Delete,
                $"/collections/{Uri.EscapeDataString(collection)}",
                body: null,
                ct,
                allowNotFound: true);

            lock (_collectionStateLock)
            {
                _collectionVectorSizes.Remove(collection);
            }
        }

        return existing;
    }

    private async Task EnsureCollectionAsync(string collection, int vectorSize, CancellationToken ct)
    {
        lock (_collectionStateLock)
        {
            if (_collectionVectorSizes.TryGetValue(collection, out var existingSize))
            {
                if (existingSize != vectorSize)
                {
                    throw new InvalidOperationException(
                        $"Collection '{collection}' expects vector size {existingSize}, got {vectorSize}.");
                }

                return;
            }
        }

        await _ensureCollectionMutex.WaitAsync(ct);
        try
        {
            lock (_collectionStateLock)
            {
                if (_collectionVectorSizes.TryGetValue(collection, out var knownSize))
                {
                    if (knownSize != vectorSize)
                    {
                        throw new InvalidOperationException(
                            $"Collection '{collection}' expects vector size {knownSize}, got {vectorSize}.");
                    }

                    return;
                }
            }

            var exists = await CollectionExistsAsync(collection, ct);
            if (!exists)
            {
                var createBody = new
                {
                    vectors = new
                    {
                        size = vectorSize,
                        distance = _options.QdrantDistance
                    }
                };

                await SendAsync(
                    HttpMethod.Put,
                    $"/collections/{Uri.EscapeDataString(collection)}",
                    createBody,
                    ct);
            }

            lock (_collectionStateLock)
            {
                _collectionVectorSizes[collection] = vectorSize;
            }
        }
        finally
        {
            _ensureCollectionMutex.Release();
        }
    }

    private async Task<bool> CollectionExistsAsync(string collection, CancellationToken ct)
    {
        var result = await SendAsync<QdrantCollectionExistsResult>(
            HttpMethod.Get,
            $"/collections/{Uri.EscapeDataString(collection)}/exists",
            body: null,
            ct,
            allowNotFound: true);

        return result?.Exists == true;
    }

    private async Task SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct,
        bool allowNotFound = false)
    {
        _ = await SendAsync<JsonElement>(method, path, body, ct, allowNotFound);
    }

    private async Task<T?> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct,
        bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.QdrantTimeoutSeconds)));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeoutCts.Token);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new OllamaTimeoutException("Qdrant request timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaRequestException("Qdrant is unavailable.", ex);
        }

        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new OllamaRequestException(
                $"Qdrant API error {(int)response.StatusCode}: {error}");
        }

        QdrantApiResponse<T>? wrapped;
        try
        {
            wrapped = await response.Content.ReadFromJsonAsync<QdrantApiResponse<T>>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            throw new OllamaResponseException("Failed to parse Qdrant response.", ex);
        }

        if (wrapped is null)
        {
            return default;
        }

        return wrapped.Result;
    }

    private static bool TryBuildDocument(JsonElement payloadElement, string id, out DocumentRecord document)
    {
        document = new DocumentRecord();

        if (!payloadElement.TryGetProperty("source", out var sourceElement))
        {
            return false;
        }

        if (!payloadElement.TryGetProperty("chunkIndex", out var chunkElement))
        {
            return false;
        }

        if (!payloadElement.TryGetProperty("text", out var textElement))
        {
            return false;
        }

        var source = sourceElement.GetString();
        var text = textElement.GetString();
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!chunkElement.TryGetInt32(out var chunkIndex))
        {
            return false;
        }

        document = new DocumentRecord
        {
            Id = id,
            Source = source,
            ChunkIndex = chunkIndex,
            Text = text,
            Embedding = Array.Empty<float>()
        };
        return true;
    }

    private static IReadOnlyList<string> BuildManagedCollections(RagOptions options)
    {
        var buckets = options.CollectionBuckets
            ?.Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();

        if (buckets.Count == 0)
        {
            buckets.AddRange(new[] { "dotnet", "javascript", "react" });
        }

        buckets.Add(CollectionBucketResolver.DefaultMiscBucket);

        return buckets
            .Select(bucket => CollectionBucketResolver.BuildCollectionName(options.QdrantCollectionPrefix, bucket))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed class QdrantApiResponse<T>
    {
        public T? Result { get; set; }
    }

    private sealed class QdrantCollectionExistsResult
    {
        public bool Exists { get; set; }
    }

    private sealed class QdrantCountResult
    {
        public long Count { get; set; }
    }

    private sealed class QdrantSearchResult
    {
        public JsonElement Id { get; set; }
        public float Score { get; set; }
        public JsonElement? Payload { get; set; }
    }
}
