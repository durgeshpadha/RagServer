namespace RagServer.Api.Tests;

public class CollectionBucketResolverTests
{
    [Fact]
    public void ResolveBucket_UsesTopLevelFolder_WhenBucketConfigured()
    {
        var root = Path.Combine(Path.GetTempPath(), "kb-root");
        var source = Path.Combine(root, "dotnet", "docs", "a.md");
        var bucket = CollectionBucketResolver.ResolveBucket(root, source, new[] { "dotnet", "javascript", "react" });

        Assert.Equal("dotnet", bucket);
    }

    [Fact]
    public void ResolveBucket_UsesMisc_WhenTopLevelNotConfigured()
    {
        var root = Path.Combine(Path.GetTempPath(), "kb-root");
        var source = Path.Combine(root, "python", "docs", "a.md");
        var bucket = CollectionBucketResolver.ResolveBucket(root, source, new[] { "dotnet", "javascript", "react" });

        Assert.Equal(CollectionBucketResolver.DefaultMiscBucket, bucket);
    }

    [Fact]
    public void BuildCollectionName_NormalizesBucketCharacters()
    {
        var name = CollectionBucketResolver.BuildCollectionName("rag_", "Java Script");
        Assert.Equal("rag_java_script", name);
    }
}
