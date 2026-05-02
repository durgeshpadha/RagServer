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

    private sealed class TrackingOllamaHandler : HttpMessageHandler
    {
        public int EmbedCalls { get; private set; }
        public int GenerateCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/api/embed", StringComparison.OrdinalIgnoreCase))
            {
                EmbedCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"embedding\":[0.1,0.2,0.3]}")
                });
            }

            if (path.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase))
            {
                GenerateCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"response\":\"direct-answer\"}")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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
