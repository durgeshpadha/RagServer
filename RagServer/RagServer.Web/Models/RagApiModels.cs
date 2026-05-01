namespace RagServer.Web.Models;

public sealed record AskRequest(string Query);

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
