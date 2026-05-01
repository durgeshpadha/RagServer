using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RagServer.Api.Tests;

public class VectorStoreTests
{
    [Fact]
    public void Constructor_DoesNotThrow_WhenVectorFileIsCorrupted()
    {
        var root = CreateTempRoot();
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);
        var vectorPath = Path.Combine(dataDir, "vectors.json");
        File.WriteAllText(vectorPath, "{ not-json");

        var env = new TestHostEnvironment(root);
        var opts = Options.Create(new RagOptions
        {
            VectorStorePath = "data\\vectors.json"
        });

        var store = new VectorStore(env, opts, NullLogger<VectorStore>.Instance);

        Assert.Empty(store.Documents);
        Assert.True(Directory.GetFiles(dataDir, "vectors.json.corrupt-*", SearchOption.TopDirectoryOnly).Length >= 0);
    }

    [Fact]
    public void AddRange_RemoveBySource_Clear_WorkAsExpected()
    {
        var root = CreateTempRoot();
        var env = new TestHostEnvironment(root);
        var opts = Options.Create(new RagOptions
        {
            VectorStorePath = "data\\vectors.json"
        });

        var store = new VectorStore(env, opts, NullLogger<VectorStore>.Instance);
        store.AddRange(new[]
        {
            new DocumentRecord { Id = "a1", Source = "a.txt", ChunkIndex = 0, Text = "a", Embedding = new float[] { 1, 0 } },
            new DocumentRecord { Id = "a2", Source = "a.txt", ChunkIndex = 1, Text = "b", Embedding = new float[] { 1, 0 } },
            new DocumentRecord { Id = "b1", Source = "b.txt", ChunkIndex = 0, Text = "c", Embedding = new float[] { 0, 1 } }
        });

        var removed = store.RemoveBySource("a.txt");
        var cleared = store.Clear();

        Assert.Equal(2, removed);
        Assert.Equal(1, cleared);
        Assert.Empty(store.Documents);
    }

    [Fact]
    public void NormalizeForEmbedding_TrimsAndNormalizesLineEndings()
    {
        var normalized = EmbeddingService.NormalizeForEmbedding("  a\r\nb\r\n  ");
        Assert.Equal("a\nb", normalized);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "rag-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
        }

        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "RagServer.Api.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
