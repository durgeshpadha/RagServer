using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RagServer.API.Controllers;

namespace RagServer.Api.Tests;

public class RagControllerTests
{
    [Fact]
    public void IngestStart_WhenRunning_Returns409()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);
        var first = setup.Controller.IngestStart();
        Assert.IsType<OkObjectResult>(first.Result);

        var second = setup.Controller.IngestStart();
        Assert.IsType<ConflictObjectResult>(second.Result);
    }

    [Fact]
    public void IngestCancel_UnknownOperation_Returns404()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);
        var result = setup.Controller.IngestCancel("missing-op");
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public void IngestCancel_CompletedOperation_Returns409()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);

        var start = setup.Controller.IngestStart();
        var ok = Assert.IsType<OkObjectResult>(start.Result);
        var startPayload = Assert.IsType<IngestStartResponse>(ok.Value);

        Assert.True(setup.Registry.TryGet(startPayload.OperationId, out var operation));
        setup.Registry.MarkCompleted(operation!, new IngestResponse(
            Message: "Ingestion complete",
            FilesScanned: 0,
            FilesIndexed: 0,
            ChunksAdded: 0,
            Failures: Array.Empty<IngestFailure>(),
            TotalStored: 0,
            DurationMs: 0));

        var cancel = setup.Controller.IngestCancel(startPayload.OperationId);
        Assert.IsType<ConflictObjectResult>(cancel.Result);
    }

    [Fact]
    public async Task Ask_UseKnowledgeBaseFalse_BypassesEmbedding_AndReturnsNoCitations()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);
        var request = new AskRequest("Explain cosine similarity.", setup.Options.GenerationModel, UseKnowledgeBase: false);

        var result = await setup.Controller.Ask(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

        Assert.Equal(1, setup.Handler.GenerateCalls);
        Assert.Equal(0, setup.Handler.EmbedCalls);
        Assert.Equal("direct-answer", json.RootElement.GetProperty("answer").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("citations").GetArrayLength());
    }

    [Fact]
    public async Task Ask_WithHistory_IncludesConversationInPrompt()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);
        var history = new[]
        {
            new ChatTurn("user", "do you have knowledge about .net 10?"),
            new ChatTurn("assistant", "Yes, I do."),
        };
        var request = new AskRequest("tell me about it", setup.Options.GenerationModel, UseKnowledgeBase: false, History: history);

        await setup.Controller.Ask(request, CancellationToken.None);

        var prompt = Assert.Single(setup.Handler.GeneratePrompts);
        Assert.Contains("User: do you have knowledge about .net 10?", prompt, StringComparison.Ordinal);
        Assert.Contains("Assistant: Yes, I do.", prompt, StringComparison.Ordinal);
        Assert.Contains("Current Question: tell me about it", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_WithMoreThanTenHistoryItems_UsesOnlyLastTen()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);
        var history = Enumerable.Range(1, 12)
            .Select(i => new ChatTurn("user", $"msg-{i}"))
            .ToArray();
        var request = new AskRequest("latest?", setup.Options.GenerationModel, UseKnowledgeBase: false, History: history);

        await setup.Controller.Ask(request, CancellationToken.None);

        var prompt = Assert.Single(setup.Handler.GeneratePrompts);
        var historyLines = prompt
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("User: msg-", StringComparison.Ordinal))
            .ToList();

        Assert.DoesNotContain("User: msg-1", historyLines);
        Assert.DoesNotContain("User: msg-2", historyLines);
        Assert.Contains("User: msg-3", historyLines);
        Assert.Contains("User: msg-12", historyLines);
    }

    [Fact]
    public async Task Ask_UseKnowledgeBaseTrue_WithNoDocs_ReturnsFallbackMessage()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);
        var request = new AskRequest("What does this project do?", setup.Options.GenerationModel, UseKnowledgeBase: true);

        var result = await setup.Controller.Ask(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

        Assert.Equal(1, setup.Handler.EmbedCalls);
        Assert.Equal(0, setup.Handler.GenerateCalls);
        Assert.Equal("I couldn't find relevant information in the knowledge base.", json.RootElement.GetProperty("answer").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("citations").GetArrayLength());
    }

    [Fact]
    public async Task Ask_UseKnowledgeBaseTrue_WithHistoryAndDocs_UsesRetrievalAndCitations()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: true);
        var history = new[]
        {
            new ChatTurn("user", "What is in the sample document?"),
            new ChatTurn("assistant", "I can check the indexed file.")
        };
        var request = new AskRequest("tell me about it", setup.Options.GenerationModel, UseKnowledgeBase: true, History: history);

        var result = await setup.Controller.Ask(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));

        Assert.Equal(1, setup.Handler.EmbedCalls);
        Assert.Equal(1, setup.Handler.GenerateCalls);
        Assert.True(json.RootElement.GetProperty("citations").GetArrayLength() > 0);

        var prompt = Assert.Single(setup.Handler.GeneratePrompts);
        Assert.Contains("Conversation History:", prompt, StringComparison.Ordinal);
        Assert.Contains("Context:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ask_InvalidModel_ReturnsBadRequest()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);
        var request = new AskRequest("hi", "not-configured-model", UseKnowledgeBase: false);

        var result = await setup.Controller.Ask(request, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ErrorResponse>(bad.Value);
        Assert.Equal("invalid_model", error.Code);
    }

    [Fact]
    public async Task IngestStream_EmitsProgressAndCompletedEvents()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: true);
        var httpContext = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;
        setup.Controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        await setup.Controller.IngestStream(CancellationToken.None);

        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        var payload = await reader.ReadToEndAsync();

        Assert.Contains("event: progress", payload, StringComparison.Ordinal);
        Assert.Contains("event: completed", payload, StringComparison.Ordinal);
        var jsonPayloads = payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line["data:".Length..].Trim())
            .ToList();

        var progressEvents = new List<int>();
        foreach (var json in jsonPayloads)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Status", out var statusEl)
                && string.Equals(statusEl.GetString(), "indexed", StringComparison.OrdinalIgnoreCase))
            {
                progressEvents.Add(doc.RootElement.GetProperty("CompletedFiles").GetInt32());
            }
        }

        Assert.Contains(jsonPayloads, json =>
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("Percent", out var percentEl)
                && percentEl.GetInt32() == 100;
        });
        Assert.True(progressEvents.SequenceEqual(progressEvents.OrderBy(v => v)));
        Assert.True(setup.Handler.EmbedCalls > 0);
    }

    [Fact]
    public async Task Ingest_ExcludesDotFolders_AndYamlFiles()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false, configureKnowledgeBase: kbPath =>
        {
            File.WriteAllText(Path.Combine(kbPath, "keep.md"), "keep");
            File.WriteAllText(Path.Combine(kbPath, "skip.yaml"), "skip");
            File.WriteAllText(Path.Combine(kbPath, "skip.yml"), "skip");

            var docs = Path.Combine(kbPath, "docs");
            Directory.CreateDirectory(docs);
            File.WriteAllText(Path.Combine(docs, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(docs, "skip.yaml"), "skip");

            var hidden = Path.Combine(kbPath, ".github");
            Directory.CreateDirectory(hidden);
            File.WriteAllText(Path.Combine(hidden, "hidden.md"), "hidden");

            var nestedHidden = Path.Combine(docs, ".cache");
            Directory.CreateDirectory(nestedHidden);
            File.WriteAllText(Path.Combine(nestedHidden, "nested-hidden.cs"), "hidden");
        });

        var result = await setup.Controller.Ingest(CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<IngestResponse>(ok.Value);

        Assert.Equal(2, response.FilesScanned);
        Assert.Equal(2, response.FilesIndexed);
        Assert.Empty(response.Failures);
    }

    [Fact]
    public async Task Ask_WithoutHistory_RemainsBackwardCompatible()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);
        var request = new AskRequest("simple question", setup.Options.GenerationModel, UseKnowledgeBase: false);

        var result = await setup.Controller.Ask(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        Assert.Equal("direct-answer", json.RootElement.GetProperty("answer").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("citations").GetArrayLength());
    }

    [Fact]
    public async Task AskStream_KbOff_EmitsTokenThenCompleted_WithEmptyCitations()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);
        var request = new AskRequest("stream this answer", setup.Options.GenerationModel, UseKnowledgeBase: false);
        var payload = await InvokeAskStreamAsync(setup.Controller, request);

        Assert.Contains("event: token", payload, StringComparison.Ordinal);
        Assert.Contains("event: completed", payload, StringComparison.Ordinal);
        Assert.Contains("\"Citations\":[]", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskStream_KbOn_EmitsTokenThenCompleted_WithCitations()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: true);
        var request = new AskRequest("stream with kb", setup.Options.GenerationModel, UseKnowledgeBase: true);
        var payload = await InvokeAskStreamAsync(setup.Controller, request);

        Assert.Contains("event: token", payload, StringComparison.Ordinal);
        Assert.Contains("event: completed", payload, StringComparison.Ordinal);
        Assert.Contains("\"Citations\":[", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Citations\":[]", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskStream_InvalidModel_ProducesValidationFailure()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);
        var request = new AskRequest("invalid model stream", "bad-model", UseKnowledgeBase: false);

        var httpContext = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;
        setup.Controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        await setup.Controller.AskStream(request, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task AskStream_UsesNormalizedHistory_Last10Only()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);
        var history = Enumerable.Range(1, 12)
            .Select(i => new ChatTurn("user", $"stream-msg-{i}"))
            .ToArray();
        var request = new AskRequest("check history", setup.Options.GenerationModel, UseKnowledgeBase: false, History: history);

        await InvokeAskStreamAsync(setup.Controller, request);

        var prompt = Assert.Single(setup.Handler.GeneratePrompts);
        var historyLines = prompt
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("User: stream-msg-", StringComparison.Ordinal))
            .ToList();

        Assert.DoesNotContain("User: stream-msg-1", historyLines);
        Assert.DoesNotContain("User: stream-msg-2", historyLines);
        Assert.Contains("User: stream-msg-3", historyLines);
        Assert.Contains("User: stream-msg-12", historyLines);
    }

    [Fact]
    public async Task AskStream_ErrorPath_EmitsErrorEvent()
    {
        var setup = CreateControllerSetup(addKnowledgeBaseDocument: false);
        var request = new AskRequest("trigger stream error", setup.Options.GenerationModel, UseKnowledgeBase: false);
        var payload = await InvokeAskStreamAsync(setup.Controller, request);

        Assert.Contains("event: error", payload, StringComparison.Ordinal);
    }

    private static ControllerSetup CreateControllerSetup(
        bool addKnowledgeBaseDocument,
        Action<string>? configureKnowledgeBase = null,
        Action<RagOptions>? configureOptions = null)
    {
        var root = CreateTempRoot();
        var kbPath = Path.Combine(root, "kb");
        Directory.CreateDirectory(kbPath);

        if (addKnowledgeBaseDocument)
        {
            File.WriteAllText(Path.Combine(kbPath, "sample.txt"), "This is a sample knowledge base document.");
        }

        configureKnowledgeBase?.Invoke(kbPath);

        var env = new TestWebHostEnvironment(root);
        var options = new RagOptions
        {
            KnowledgeBasePath = "kb",
            VectorStorePath = "data\\vectors.json",
            OllamaBaseUrl = "http://localhost:11434",
            EmbeddingModel = "nomic-embed-text",
            GenerationModel = "deepseek-coder-v2:16b",
            GenerationModels = new[] { "deepseek-coder-v2:16b", "qwen2.5-coder:14b" },
            TopK = 5,
            MaxContextChars = 4000,
            IngestMaxParallelFiles = 2,
            IngestMaxParallelEmbeddingsPerFile = 2
        };
        configureOptions?.Invoke(options);

        var optionsWrapper = Options.Create(options);
        var handler = new TrackingOllamaHandler();
        var httpClient = new HttpClient(handler);
        var embeddingService = new EmbeddingService(httpClient, optionsWrapper);
        var vectorStore = new InMemoryVectorStore(options);
        if (addKnowledgeBaseDocument)
        {
            vectorStore.UpsertAsync(
                vectorStore.ResolveCollection(kbPath, Path.Combine(kbPath, "sample.txt")),
                new[]
                {
                    new DocumentRecord
                    {
                        Id = "seed-1",
                        Source = Path.Combine(kbPath, "sample.txt"),
                        ChunkIndex = 0,
                        Text = "This is a sample knowledge base document.",
                        Embedding = new[] { 0.1f, 0.2f, 0.3f }
                    }
                },
                CancellationToken.None).GetAwaiter().GetResult();
        }
        var ragEngine = new RagEngine(embeddingService, vectorStore, httpClient, optionsWrapper);
        var registry = new IngestOperationRegistry();
        var controller = new RagController(
            embeddingService,
            vectorStore,
            ragEngine,
            registry,
            optionsWrapper,
            env,
            NullLogger<RagController>.Instance);

        return new ControllerSetup(controller, handler, options, registry);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "rag-controller-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record ControllerSetup(
        RagController Controller,
        TrackingOllamaHandler Handler,
        RagOptions Options,
        IngestOperationRegistry Registry);

    private static async Task<string> InvokeAskStreamAsync(RagController controller, AskRequest request)
    {
        var httpContext = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        await controller.AskStream(request, CancellationToken.None);

        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        return await reader.ReadToEndAsync();
    }

    private sealed class TrackingOllamaHandler : HttpMessageHandler
    {
        public int EmbedCalls { get; private set; }
        public int GenerateCalls { get; private set; }
        public List<string> GeneratePrompts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/api/embed", StringComparison.OrdinalIgnoreCase))
            {
                EmbedCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"embedding\":[0.1,0.2,0.3]}")
                };
            }

            if (path.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase))
            {
                GenerateCalls++;
                var payload = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);

                var isStreaming = false;

                if (!string.IsNullOrWhiteSpace(payload))
                {
                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("prompt", out var promptEl))
                    {
                        var prompt = promptEl.GetString() ?? string.Empty;
                        GeneratePrompts.Add(prompt);
                    }

                    if (doc.RootElement.TryGetProperty("stream", out var streamEl))
                    {
                        isStreaming = streamEl.ValueKind == JsonValueKind.True;
                    }
                }

                if (isStreaming)
                {
                    var hasErrorTrigger = GeneratePrompts.LastOrDefault()?.Contains("trigger stream error", StringComparison.OrdinalIgnoreCase) == true;
                    if (hasErrorTrigger)
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent("{\"error\":\"simulated stream error\",\"done\":true}\n")
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"response\":\"direct-\",\"done\":false}\n{\"response\":\"answer\",\"done\":false}\n{\"done\":true}\n")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"response\":\"direct-answer\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class InMemoryVectorStore : IVectorStore
    {
        private readonly RagOptions _options;
        private readonly List<(string Collection, DocumentRecord Record)> _records = new();
        private readonly Lock _lock = new();

        public InMemoryVectorStore(RagOptions options)
        {
            _options = options;
            ManagedCollections = options.CollectionBuckets
                .Concat(new[] { CollectionBucketResolver.DefaultMiscBucket })
                .Select(bucket => CollectionBucketResolver.BuildCollectionName(options.QdrantCollectionPrefix, bucket))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public IReadOnlyList<string> ManagedCollections { get; }

        public string ResolveCollection(string rootPath, string sourcePath)
        {
            var bucket = CollectionBucketResolver.ResolveBucket(rootPath, sourcePath, _options.CollectionBuckets);
            return CollectionBucketResolver.BuildCollectionName(_options.QdrantCollectionPrefix, bucket);
        }

        public Task DeleteBySourceAsync(string collection, string source, CancellationToken ct = default)
        {
            lock (_lock)
            {
                _records.RemoveAll(r =>
                    string.Equals(r.Collection, collection, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Record.Source, source, StringComparison.OrdinalIgnoreCase));
            }

            return Task.CompletedTask;
        }

        public Task UpsertAsync(string collection, IReadOnlyList<DocumentRecord> records, CancellationToken ct = default)
        {
            lock (_lock)
            {
                foreach (var record in records)
                {
                    _records.RemoveAll(r =>
                        string.Equals(r.Collection, collection, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.Record.Id, record.Id, StringComparison.OrdinalIgnoreCase));

                    _records.Add((collection, record));
                }
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScoredDocumentRecord>> QueryAsync(float[] queryEmbedding, int topK, CancellationToken ct = default)
        {
            List<(string Collection, DocumentRecord Record)> snapshot;
            lock (_lock)
            {
                snapshot = _records.ToList();
            }

            var scored = snapshot
                .Select(item => new ScoredDocumentRecord(item.Record, CosineSimilarity(queryEmbedding, item.Record.Embedding), item.Collection))
                .OrderByDescending(s => s.Score)
                .Take(topK)
                .ToArray();

            return Task.FromResult<IReadOnlyList<ScoredDocumentRecord>>(scored);
        }

        public Task<long> CountAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult((long)_records.Count);
            }
        }

        public async Task<long> ClearAsync(CancellationToken ct = default)
        {
            var count = await CountAsync(ct);
            lock (_lock)
            {
                _records.Clear();
            }

            return count;
        }

        private static float CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length || a.Length == 0)
            {
                return -1f;
            }

            double dot = 0;
            double na = 0;
            double nb = 0;
            for (var i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }

            if (na == 0 || nb == 0)
            {
                return 0f;
            }

            return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
            WebRootPath = contentRootPath;
            WebRootFileProvider = new PhysicalFileProvider(contentRootPath);
        }

        public string ApplicationName { get; set; } = "RagServer.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
