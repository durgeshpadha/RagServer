/// <summary>
/// Represents a single chat message in the conversation history.
/// </summary>
/// <param name="Role">Author role for the turn, such as <c>user</c> or <c>assistant</c>.</param>
/// <param name="Content">Plain text content of the message.</param>
public record ChatTurn(string Role, string Content);

/// <summary>
/// Request payload for answering a user query.
/// </summary>
/// <param name="Query">User question or instruction text.</param>
/// <param name="Model">Optional generation model override.</param>
/// <param name="UseKnowledgeBase">Whether retrieval from the indexed knowledge base should be used.</param>
/// <param name="History">Optional recent conversation history used for contextual responses.</param>
public record AskRequest(
    string Query,
    string? Model = null,
    bool UseKnowledgeBase = true,
    IReadOnlyList<ChatTurn>? History = null);

/// <summary>
/// Describes a single file that failed during ingestion.
/// </summary>
/// <param name="File">Display path of the file that failed.</param>
/// <param name="ErrorCode">Stable machine-readable error code.</param>
/// <param name="Message">Human-readable failure detail.</param>
public record IngestFailure(string File, string ErrorCode, string Message);

/// <summary>
/// Summary returned after an ingest operation completes.
/// </summary>
/// <param name="Message">High-level operation status message.</param>
/// <param name="FilesScanned">Total number of files scanned for ingestion.</param>
/// <param name="FilesIndexed">Number of files successfully indexed.</param>
/// <param name="ChunksAdded">Total chunks written to the vector store.</param>
/// <param name="Failures">Collection of per-file failures encountered during ingest.</param>
/// <param name="TotalStored">Total records currently stored after ingest.</param>
/// <param name="DurationMs">Total ingest duration in milliseconds.</param>
public record IngestResponse(
    string Message,
    int FilesScanned,
    int FilesIndexed,
    int ChunksAdded,
    IReadOnlyList<IngestFailure> Failures,
    int TotalStored,
    long DurationMs);

/// <summary>
/// Standard API error payload.
/// </summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Message">Human-readable error message.</param>
public record ErrorResponse(string Code, string Message);

/// <summary>
/// Progress details for a single file being processed during ingest.
/// </summary>
/// <param name="File">Display path of the file currently being processed.</param>
/// <param name="CompletedChunks">Number of chunks completed for the file.</param>
/// <param name="TotalChunks">Total number of chunks planned for the file.</param>
/// <param name="Percent">Completion percentage for the current file.</param>
/// <param name="Stage">Current file stage, such as reading, embedding, upserting, or completed.</param>
public record IngestFileProgress(
    string File,
    int CompletedChunks,
    int TotalChunks,
    int Percent,
    string Stage);

/// <summary>
/// Server-sent event payload for ingest lifecycle and progress notifications.
/// </summary>
/// <param name="Status">Overall event status, such as started, progress, completed, canceled, or error.</param>
/// <param name="TotalFiles">Total number of files in scope for the ingest operation.</param>
/// <param name="CompletedFiles">Number of files completed so far.</param>
/// <param name="RemainingFiles">Number of files not yet completed.</param>
/// <param name="Percent">Overall ingest completion percentage.</param>
/// <param name="OperationId">Identifier of the ingest operation.</param>
/// <param name="CurrentFile">Display path of the current file related to this event.</param>
/// <param name="Summary">Final ingest summary when the operation completes.</param>
/// <param name="ErrorMessage">Optional error detail when the status is error or canceled.</param>
/// <param name="FileProgress">Optional per-file progress payload.</param>
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

/// <summary>
/// Response payload returned when an ingest operation is created.
/// </summary>
/// <param name="OperationId">Identifier used to stream, monitor, or cancel the ingest operation.</param>
public record IngestStartResponse(string OperationId);

/// <summary>
/// Response payload returned when ingest cancellation is requested.
/// </summary>
/// <param name="OperationId">Identifier of the targeted ingest operation.</param>
/// <param name="Status">Current operation status after the cancel request.</param>
/// <param name="Message">Human-readable cancellation status message.</param>
public record IngestCancelResponse(string OperationId, string Status, string Message);

/// <summary>
/// Streaming token event emitted while generating an answer.
/// </summary>
/// <param name="Text">Latest generated text token fragment.</param>
public record AskStreamTokenEvent(string Text);

/// <summary>
/// Final stream event emitted after answer generation completes.
/// </summary>
/// <param name="Answer">Final generated answer text.</param>
/// <param name="Citations">Supporting citations used for the answer.</param>
public record AskStreamCompletedEvent(string Answer, IReadOnlyList<Citation> Citations);

/// <summary>
/// Stream error event emitted when answer generation fails.
/// </summary>
/// <param name="Message">Human-readable error message.</param>
/// <param name="Code">Optional machine-readable error code.</param>
public record AskStreamErrorEvent(string Message, string? Code = null);

/// <summary>
/// Configuration options for RAG ingestion, retrieval, and generation behavior.
/// </summary>
public class RagOptions
{
    /// <summary>
    /// Configuration section name used for binding.
    /// </summary>
    public const string SectionName = "Rag";

    /// <summary>
    /// Path to the source knowledge base files for ingestion.
    /// </summary>
    public string KnowledgeBasePath { get; set; } = "..\\RAG-KnowledgeBase";

    /// <summary>
    /// Legacy local vector store path when file-based storage is used.
    /// </summary>
    public string VectorStorePath { get; set; } = "..\\data\\vectors.json";

    /// <summary>
    /// Base URL for the Qdrant service.
    /// </summary>
    public string QdrantUrl { get; set; } = "http://localhost:6333";

    /// <summary>
    /// Prefix used when composing Qdrant collection names.
    /// </summary>
    public string QdrantCollectionPrefix { get; set; } = "rag_";

    /// <summary>
    /// Collection bucket names used to partition indexed data.
    /// </summary>
    public string[] CollectionBuckets { get; set; } = new[] { "dotnet", "javascript", "react" };

    /// <summary>
    /// Qdrant vector distance metric name.
    /// </summary>
    public string QdrantDistance { get; set; } = "Cosine";

    /// <summary>
    /// Maximum number of vectors upserted per Qdrant batch.
    /// </summary>
    public int QdrantUpsertBatchSize { get; set; } = 64;

    /// <summary>
    /// Timeout in seconds for Qdrant requests.
    /// </summary>
    public int QdrantTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Base URL for the Ollama service.
    /// </summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Embedding model name used during ingestion and retrieval.
    /// </summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>
    /// Default generation model used for responses.
    /// </summary>
    public string GenerationModel { get; set; } = "deepseek-coder-v2";

    /// <summary>
    /// Explicit allow-list of generation models for API callers.
    /// </summary>
    public string[] GenerationModels { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Maximum allowed query length in characters.
    /// </summary>
    public int MaxQueryChars { get; set; } = 4000;

    /// <summary>
    /// Number of top matching chunks retrieved for context.
    /// </summary>
    public int TopK { get; set; } = 5;

    /// <summary>
    /// Maximum total context length passed to generation.
    /// </summary>
    public int MaxContextChars { get; set; } = 8000;

    /// <summary>
    /// Target chunk size in characters during ingestion.
    /// </summary>
    public int ChunkSizeChars { get; set; } = 1000;

    /// <summary>
    /// Number of overlapping characters between adjacent chunks.
    /// </summary>
    public int ChunkOverlapChars { get; set; } = 200;

    /// <summary>
    /// Maximum ingest file size in bytes; set to 0 for no limit.
    /// </summary>
    public long MaxIngestFileBytes { get; set; } = 0;

    /// <summary>
    /// Maximum ingest file length in characters; set to 0 for no limit.
    /// </summary>
    public int MaxIngestFileChars { get; set; } = 0;

    /// <summary>
    /// Maximum number of files processed concurrently during ingest.
    /// </summary>
    public int IngestMaxParallelFiles { get; set; } = 2;

    /// <summary>
    /// Maximum number of embedding operations run concurrently per file.
    /// </summary>
    public int IngestMaxParallelEmbeddingsPerFile { get; set; } = 2;
}
