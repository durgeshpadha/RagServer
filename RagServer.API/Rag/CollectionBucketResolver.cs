public static class CollectionBucketResolver
{
    public const string DefaultMiscBucket = "misc";

    public static string ResolveBucket(string rootPath, string sourcePath, IReadOnlyList<string>? configuredBuckets)
    {
        var buckets = configuredBuckets?
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (buckets.Count == 0)
        {
            buckets = new HashSet<string>(new[] { "dotnet", "javascript", "react" }, StringComparer.OrdinalIgnoreCase);
        }

        var normalizedRoot = Path.GetFullPath(rootPath);
        var normalizedSource = Path.GetFullPath(sourcePath);

        var relative = Path.GetRelativePath(normalizedRoot, normalizedSource);
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return DefaultMiscBucket;
        }

        var firstSegment = relative
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(firstSegment))
        {
            return DefaultMiscBucket;
        }

        return buckets.Contains(firstSegment) ? firstSegment : DefaultMiscBucket;
    }

    public static string BuildCollectionName(string prefix, string bucket)
    {
        var normalizedPrefix = string.IsNullOrWhiteSpace(prefix) ? "rag_" : prefix.Trim();
        var normalizedBucket = string.IsNullOrWhiteSpace(bucket) ? DefaultMiscBucket : bucket.Trim();

        var sanitized = new string(normalizedBucket
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray());

        if (!normalizedPrefix.EndsWith("_", StringComparison.Ordinal))
        {
            normalizedPrefix += "_";
        }

        return $"{normalizedPrefix}{sanitized}";
    }
}
