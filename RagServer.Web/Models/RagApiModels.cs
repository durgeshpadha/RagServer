namespace RagServer.Web.Models;

public sealed record ChatTurn(string Role, string Content);

public sealed record AskRequest(
    string Query,
    string? Model = null,
    bool UseKnowledgeBase = true,
    IReadOnlyList<ChatTurn>? History = null);

public sealed record AskResponse(string Answer, IReadOnlyList<Citation> Citations);

public sealed record Citation(string Source, int ChunkIndex);

public sealed record IngestFailure(string File, string ErrorCode, string Message);

public sealed record IngestResponse(
    string Message,
    int FilesScanned,
    int FilesIndexed,
    int ChunksAdded,
    IReadOnlyList<IngestFailure> Failures,
    int TotalStored,
    long DurationMs);

public sealed record ErrorResponse(string Code, string Message);

public sealed record DataCountResponse(int TotalStored);

public sealed record ClearDataResponse(string Message, int Removed);

public sealed record ModelsResponse(string DefaultModel, IReadOnlyList<string> Models);

public sealed record IngestProgressEvent(
    string Status,
    int TotalFiles,
    int CompletedFiles,
    int RemainingFiles,
    int Percent,
    string? CurrentFile = null,
    IngestResponse? Summary = null,
    string? ErrorMessage = null);

public sealed record AskStreamTokenEvent(string Text);
public sealed record AskStreamCompletedEvent(string Answer, IReadOnlyList<Citation> Citations);
public sealed record AskStreamErrorEvent(string Message, string? Code = null);

public sealed record AskStreamEvent(
    string EventType,
    AskStreamTokenEvent? Token = null,
    AskStreamCompletedEvent? Completed = null,
    AskStreamErrorEvent? Error = null);
