public record ChatTurn(string Role, string Content);

public record AskRequest(
    string Query,
    string? Model = null,
    bool UseKnowledgeBase = true,
    IReadOnlyList<ChatTurn>? History = null);

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

public record IngestFileProgress(
    string File,
    int CompletedChunks,
    int TotalChunks,
    int Percent,
    string Stage);

public record IngestProgressEvent(
    string Status,
    int TotalFiles,
    int CompletedFiles,
    int RemainingFiles,
    int Percent,
    string? OperationId = null,
    string? CurrentFile = null,
    IngestResponse? Summary = null,
    string? ErrorMessage = null,
    IngestFileProgress? FileProgress = null);

public record IngestStartResponse(string OperationId);
public record IngestCancelResponse(string OperationId, string Status, string Message);

public record AskStreamTokenEvent(string Text);
public record AskStreamCompletedEvent(string Answer, IReadOnlyList<Citation> Citations);
public record AskStreamErrorEvent(string Message, string? Code = null);

public class RagOptions
{
    public const string SectionName = "Rag";
    public string KnowledgeBasePath { get; set; } = "..\\RAG-KnowledgeBase";
    public string VectorStorePath { get; set; } = "..\\data\\vectors.json";
    public string QdrantUrl { get; set; } = "http://localhost:6333";
    public string QdrantCollectionPrefix { get; set; } = "rag_";
    public string[] CollectionBuckets { get; set; } = new[] { "dotnet", "javascript", "react" };
    public string QdrantDistance { get; set; } = "Cosine";
    public int QdrantUpsertBatchSize { get; set; } = 64;
    public int QdrantTimeoutSeconds { get; set; } = 30;
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public string GenerationModel { get; set; } = "deepseek-coder-v2";
    public string[] GenerationModels { get; set; } = Array.Empty<string>();
    public int MaxQueryChars { get; set; } = 4000;
    public int TopK { get; set; } = 5;
    public int MaxContextChars { get; set; } = 8000;
    public int ChunkSizeChars { get; set; } = 1000;
    public int ChunkOverlapChars { get; set; } = 200;
    public long MaxIngestFileBytes { get; set; } = 0;
    public int MaxIngestFileChars { get; set; } = 0;
    public int IngestMaxParallelFiles { get; set; } = 2;
    public int IngestMaxParallelEmbeddingsPerFile { get; set; } = 2;
}
