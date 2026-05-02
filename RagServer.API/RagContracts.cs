public record AskRequest(string Query, string? Model = null);

public record IngestFailure(string File, string ErrorCode, string Message);

public record IngestResponse(
    string Message,
    int FilesScanned,
    int FilesIndexed,
    int ChunksAdded,
    IReadOnlyList<IngestFailure> Failures,
    int TotalStored,
    long DurationMs);

public record ErrorResponse(string Code, string Message);

public class RagOptions
{
    public const string SectionName = "Rag";
    public string KnowledgeBasePath { get; set; } = "..\\RAG-KnowledgeBase";
    public string VectorStorePath { get; set; } = "..\\data\\vectors.json";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public string GenerationModel { get; set; } = "deepseek-coder-v2";
    public string[] GenerationModels { get; set; } = Array.Empty<string>();
    public int MaxQueryChars { get; set; } = 4000;
    public int TopK { get; set; } = 5;
    public int MaxContextChars { get; set; } = 8000;
    public int ChunkSizeChars { get; set; } = 1000;
    public int ChunkOverlapChars { get; set; } = 200;
}
