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

        Assert.Contains(jsonPayloads, json =>
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("Percent", out var percentEl)
                && percentEl.GetInt32() == 100;
        });
        Assert.True(setup.Handler.EmbedCalls > 0);
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

    private static ControllerSetup CreateControllerSetup(bool addKnowledgeBaseDocument)
    {
        var root = CreateTempRoot();
        var kbPath = Path.Combine(root, "kb");
        Directory.CreateDirectory(kbPath);

        if (addKnowledgeBaseDocument)
        {
            File.WriteAllText(Path.Combine(kbPath, "sample.txt"), "This is a sample knowledge base document.");
        }

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
            MaxContextChars = 4000
        };

        var optionsWrapper = Options.Create(options);
        var handler = new TrackingOllamaHandler();
        var httpClient = new HttpClient(handler);
        var embeddingService = new EmbeddingService(httpClient, optionsWrapper);
        var vectorStore = new VectorStore(env, optionsWrapper, NullLogger<VectorStore>.Instance);
        if (addKnowledgeBaseDocument)
        {
            vectorStore.Add(new DocumentRecord
            {
                Id = "seed-1",
                Source = Path.Combine(kbPath, "sample.txt"),
                ChunkIndex = 0,
                Text = "This is a sample knowledge base document.",
                Embedding = new[] { 0.1f, 0.2f, 0.3f }
            });
        }
        var ragEngine = new RagEngine(embeddingService, vectorStore, httpClient, optionsWrapper);
        var controller = new RagController(
            embeddingService,
            vectorStore,
            ragEngine,
            optionsWrapper,
            env,
            NullLogger<RagController>.Instance);

        return new ControllerSetup(controller, handler, options);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "rag-controller-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record ControllerSetup(RagController Controller, TrackingOllamaHandler Handler, RagOptions Options);

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
