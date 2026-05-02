using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RagServer.Api.Tests;

public class RagEngineTests
{
    [Fact]
    public async Task PrepareWithKnowledgeBaseAsync_UsesReturnedTopKAcrossCollections()
    {
        var options = Options.Create(new RagOptions
        {
            OllamaBaseUrl = "http://localhost:11434",
            EmbeddingModel = "nomic-embed-text",
            TopK = 2,
            MaxContextChars = 4000
        });

        var handler = new FakeOllamaHandler();
        var http = new HttpClient(handler);
        var embedding = new EmbeddingService(http, options);
        var store = new FakeVectorStore();
        var engine = new RagEngine(embedding, store, http, options);

        var prepared = await engine.PrepareWithKnowledgeBaseAsync("what is this?", history: null, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(prepared.Prompt));
        Assert.Equal(2, prepared.Citations.Count);
        Assert.Contains(prepared.Citations, c => c.Source == "a.md");
        Assert.Contains(prepared.Citations, c => c.Source == "b.md");
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public IReadOnlyList<string> ManagedCollections => new[] { "rag_dotnet", "rag_javascript" };

        public string ResolveCollection(string rootPath, string sourcePath) => "rag_dotnet";

        public Task DeleteBySourceAsync(string collection, string source, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpsertAsync(string collection, IReadOnlyList<DocumentRecord> records, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ScoredDocumentRecord>> QueryAsync(float[] queryEmbedding, int topK, CancellationToken ct = default)
        {
            var docs = new[]
            {
                new ScoredDocumentRecord(new DocumentRecord
                {
                    Id = "1",
                    Source = "D:\\repo\\a.md",
                    ChunkIndex = 0,
                    Text = "doc-a",
                    Embedding = Array.Empty<float>()
                }, 0.91f, "rag_dotnet"),
                new ScoredDocumentRecord(new DocumentRecord
                {
                    Id = "2",
                    Source = "D:\\repo\\b.md",
                    ChunkIndex = 1,
                    Text = "doc-b",
                    Embedding = Array.Empty<float>()
                }, 0.87f, "rag_javascript")
            };

            return Task.FromResult<IReadOnlyList<ScoredDocumentRecord>>(docs.Take(topK).ToArray());
        }

        public Task<long> CountAsync(CancellationToken ct = default) => Task.FromResult(0L);

        public Task<long> ClearAsync(CancellationToken ct = default) => Task.FromResult(0L);
    }

    private sealed class FakeOllamaHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/api/embed", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { embedding = new[] { 0.1f, 0.2f, 0.3f } }))
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"response\":\"ok\"}")
            });
        }
    }
}
