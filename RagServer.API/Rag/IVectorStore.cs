public interface IVectorStore
{
    IReadOnlyList<string> ManagedCollections { get; }

    string ResolveCollection(string rootPath, string sourcePath);

    Task DeleteBySourceAsync(string collection, string source, CancellationToken ct = default);

    Task UpsertAsync(string collection, IReadOnlyList<DocumentRecord> records, CancellationToken ct = default);

    Task<IReadOnlyList<ScoredDocumentRecord>> QueryAsync(float[] queryEmbedding, int topK, CancellationToken ct = default);

    Task<long> CountAsync(CancellationToken ct = default);

    Task<long> ClearAsync(CancellationToken ct = default);
}

public record ScoredDocumentRecord(DocumentRecord Document, float Score, string Collection);
