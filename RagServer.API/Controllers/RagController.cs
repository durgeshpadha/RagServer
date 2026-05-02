using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RagServer.API.Controllers;

[ApiController]
[Route("")]
public class RagController : ControllerBase
{
    private const int MaxHistoryItems = 10;
    private readonly EmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly RagEngine _ragEngine;
    private readonly IngestOperationRegistry _ingestRegistry;
    private readonly IOptions<RagOptions> _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<RagController> _logger;

    public RagController(
        EmbeddingService embeddingService,
        IVectorStore vectorStore,
        RagEngine ragEngine,
        IngestOperationRegistry ingestRegistry,
        IOptions<RagOptions> options,
        IWebHostEnvironment environment,
        ILogger<RagController> logger)
    {
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _ragEngine = ragEngine;
        _ingestRegistry = ingestRegistry;
        _options = options;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Scans the configured knowledge base, chunks supported files, generates embeddings, and stores vectors.
    /// </summary>
    /// <param name="ct">Cancellation token for stopping ingestion.</param>
    /// <returns>Ingestion summary including counts, failures, and duration.</returns>
    /// <response code="200">Ingestion completed and summary returned.</response>
    /// <response code="400">Knowledge base path is missing or invalid.</response>
    [HttpPost("ingest")]
    public async Task<ActionResult<IngestResponse>> Ingest(CancellationToken ct)
    {
        if (!_ingestRegistry.TryStart(out var operation, out var conflict))
        {
            return Conflict(new ErrorResponse("ingest_already_running", $"Ingest already running. OperationId: {conflict!.OperationId}"));
        }

        if (!_ingestRegistry.TryMarkRunning(operation))
        {
            return Conflict(new ErrorResponse("ingest_not_startable", "Ingest operation could not be started."));
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, operation.Cancellation.Token);
        try
        {
            var result = await RunIngestAsync(operation.OperationId, progressCallback: null, linkedCts.Token);
            _ingestRegistry.MarkCompleted(operation, result);
            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            _ingestRegistry.MarkCanceled(operation);
            return Conflict(new ErrorResponse("ingest_canceled", "Ingest was canceled."));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Knowledge base directory not found.", StringComparison.Ordinal))
        {
            _ingestRegistry.MarkFailed(operation, ex.Message);
            return BadRequest(new ErrorResponse("knowledge_base_not_found", "Knowledge base directory not found."));
        }
        catch (Exception ex)
        {
            _ingestRegistry.MarkFailed(operation, ex.Message);
            throw;
        }
    }

    [HttpPost("ingest/start")]
    public ActionResult<IngestStartResponse> IngestStart()
    {
        if (!_ingestRegistry.TryStart(out var operation, out var conflict))
        {
            return Conflict(new ErrorResponse("ingest_already_running", $"Ingest already running. OperationId: {conflict!.OperationId}"));
        }

        return Ok(new IngestStartResponse(operation.OperationId));
    }

    [HttpPost("ingest/{operationId}/cancel")]
    public ActionResult<IngestCancelResponse> IngestCancel(string operationId)
    {
        if (!_ingestRegistry.TryCancel(operationId, out var operation) || operation is null)
        {
            return NotFound(new ErrorResponse("ingest_not_found", "Ingest operation not found."));
        }

        if (operation.Status is IngestOperationStatus.Completed or IngestOperationStatus.Canceled or IngestOperationStatus.Failed)
        {
            return Conflict(new ErrorResponse("ingest_not_running", $"Operation is already {operation.Status.ToString().ToLowerInvariant()}."));
        }

        return Ok(new IngestCancelResponse(operation.OperationId, operation.Status.ToString().ToLowerInvariant(), "Cancellation requested."));
    }

    [HttpGet("ingest/{operationId}/stream")]
    public async Task IngestOperationStream(string operationId, CancellationToken ct)
    {
        if (!_ingestRegistry.TryGet(operationId, out var operation))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.WriteAsJsonAsync(new ErrorResponse("ingest_not_found", "Ingest operation not found."), ct);
            return;
        }

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        if (operation.Status == IngestOperationStatus.Completed && operation.Summary is not null)
        {
            var completed = new IngestProgressEvent(
                "completed",
                operation.Summary.FilesScanned,
                operation.Summary.FilesScanned,
                0,
                100,
                operation.OperationId,
                Summary: operation.Summary);
            await WriteSseEventAsync("completed", completed, ct);
            return;
        }

        if (operation.Status == IngestOperationStatus.Canceled)
        {
            var canceled = new IngestProgressEvent("canceled", 0, 0, 0, 0, operation.OperationId, ErrorMessage: "Ingest was canceled.");
            await WriteSseEventAsync("canceled", canceled, ct);
            return;
        }

        if (operation.Status == IngestOperationStatus.Failed)
        {
            var failed = new IngestProgressEvent("error", 0, 0, 0, 0, operation.OperationId, ErrorMessage: operation.ErrorMessage ?? "Ingest failed.");
            await WriteSseEventAsync("error", failed, ct);
            return;
        }

        if (!_ingestRegistry.TryMarkRunning(operation))
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            await Response.WriteAsJsonAsync(new ErrorResponse("ingest_not_startable", $"Operation is {operation.Status.ToString().ToLowerInvariant()}."), ct);
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, operation.Cancellation.Token);
        try
        {
            var summary = await RunIngestAsync(operation.OperationId, async progress =>
            {
                await WriteSseEventAsync("progress", progress, ct);
            }, linkedCts.Token);

            _ingestRegistry.MarkCompleted(operation, summary);

            var completed = new IngestProgressEvent(
                "completed",
                summary.FilesScanned,
                summary.FilesScanned,
                0,
                100,
                operation.OperationId,
                Summary: summary);

            await WriteSseEventAsync("completed", completed, ct);
        }
        catch (OperationCanceledException)
        {
            _ingestRegistry.MarkCanceled(operation);
            var canceled = new IngestProgressEvent("canceled", 0, 0, 0, 0, operation.OperationId, ErrorMessage: "Ingest was canceled.");
            await WriteSseEventAsync("canceled", canceled, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _ingestRegistry.MarkFailed(operation, ex.Message);
            _logger.LogError(ex, "Unhandled error during ingest stream.");
            var errorEvent = new IngestProgressEvent("error", 0, 0, 0, 0, operation.OperationId, ErrorMessage: ex.Message);
            await WriteSseEventAsync("error", errorEvent, CancellationToken.None);
        }
    }

    [HttpPost("ingest/stream")]
    public async Task IngestStream(CancellationToken ct)
    {
        if (!_ingestRegistry.TryStart(out var operation, out var conflict))
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            await Response.WriteAsJsonAsync(new ErrorResponse("ingest_already_running", $"Ingest already running. OperationId: {conflict!.OperationId}"), ct);
            return;
        }

        await IngestOperationStream(operation.OperationId, ct);
    }

    /// <summary>
    /// Answers a user query using retrieval-augmented generation over stored vectorized content.
    /// </summary>
    /// <param name="req">User query payload.</param>
    /// <param name="ct">Cancellation token for stopping the request.</param>
    /// <returns>Generated answer and supporting citations.</returns>
    /// <response code="200">Answer generated successfully.</response>
    /// <response code="400">Query payload is empty or exceeds allowed length.</response>
    /// <response code="502">Generation service returned an invalid response.</response>
    /// <response code="503">Generation service is unavailable.</response>
    /// <response code="504">Generation timed out.</response>
    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequest req, CancellationToken ct)
    {
        var ragOptions = _options.Value;

        if (req is null || string.IsNullOrWhiteSpace(req.Query))
        {
            return BadRequest(new ErrorResponse("invalid_query", "Query is required."));
        }

        if (req.Query.Length > ragOptions.MaxQueryChars)
        {
            return BadRequest(new ErrorResponse("query_too_large", $"Query must be <= {ragOptions.MaxQueryChars} characters."));
        }

        var availableModels = GetAvailableGenerationModels(ragOptions);
        var requestedModel = req.Model?.Trim();
        if (!string.IsNullOrWhiteSpace(requestedModel) &&
            !availableModels.Contains(requestedModel, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new ErrorResponse("invalid_model", "Requested model is not in the configured model list."));
        }

        var selectedModel = string.IsNullOrWhiteSpace(requestedModel)
            ? ResolveDefaultModel(ragOptions, availableModels)
            : requestedModel!;
        var history = NormalizeHistory(req.History);

        try
        {
            var result = req.UseKnowledgeBase
                ? await _ragEngine.AskWithKnowledgeBaseAsync(req.Query, selectedModel, history, ct)
                : await _ragEngine.AskDirectAsync(req.Query, selectedModel, history, ct);
            return Ok(new { answer = result.Answer, citations = result.Citations });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OllamaTimeoutException ex)
        {
            _logger.LogWarning(ex, "Generation timed out for /ask request.");
            return StatusCode(StatusCodes.Status504GatewayTimeout);
        }
        catch (OllamaRequestException ex)
        {
            _logger.LogWarning(ex, "Generation request failed for /ask request.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (OllamaResponseException ex)
        {
            _logger.LogWarning(ex, "Generation response invalid for /ask request.");
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    [HttpPost("ask/stream")]
    public async Task AskStream([FromBody] AskRequest req, CancellationToken ct)
    {
        var ragOptions = _options.Value;

        if (req is null || string.IsNullOrWhiteSpace(req.Query))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new ErrorResponse("invalid_query", "Query is required."), ct);
            return;
        }

        if (req.Query.Length > ragOptions.MaxQueryChars)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new ErrorResponse("query_too_large", $"Query must be <= {ragOptions.MaxQueryChars} characters."), ct);
            return;
        }

        var availableModels = GetAvailableGenerationModels(ragOptions);
        var requestedModel = req.Model?.Trim();
        if (!string.IsNullOrWhiteSpace(requestedModel) &&
            !availableModels.Contains(requestedModel, StringComparer.OrdinalIgnoreCase))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsJsonAsync(new ErrorResponse("invalid_model", "Requested model is not in the configured model list."), ct);
            return;
        }

        var selectedModel = string.IsNullOrWhiteSpace(requestedModel)
            ? ResolveDefaultModel(ragOptions, availableModels)
            : requestedModel!;
        var history = NormalizeHistory(req.History);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        try
        {
            RagEngine.AskPreparation prepared;
            if (req.UseKnowledgeBase)
            {
                prepared = await _ragEngine.PrepareWithKnowledgeBaseAsync(req.Query, history, ct);
            }
            else
            {
                prepared = _ragEngine.PrepareDirect(req.Query, history);
            }

            if (prepared.ShortCircuitResult is not null)
            {
                var shortCircuit = new AskStreamCompletedEvent(prepared.ShortCircuitResult.Answer, prepared.ShortCircuitResult.Citations);
                await WriteAskSseEventAsync("completed", shortCircuit, ct);
                return;
            }

            var full = new StringBuilder();
            await foreach (var token in _ragEngine.StreamGenerateAsync(prepared.Prompt, selectedModel, ct))
            {
                full.Append(token);
                await WriteAskSseEventAsync("token", new AskStreamTokenEvent(token), ct);
            }

            await WriteAskSseEventAsync("completed", new AskStreamCompletedEvent(full.ToString(), prepared.Citations), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // client disconnected/cancelled
        }
        catch (OllamaTimeoutException ex)
        {
            _logger.LogWarning(ex, "Generation timed out for /ask/stream request.");
            await WriteAskSseEventAsync("error", new AskStreamErrorEvent("Generation timed out.", "timeout"), CancellationToken.None);
        }
        catch (OllamaRequestException ex)
        {
            _logger.LogWarning(ex, "Generation request failed for /ask/stream request.");
            await WriteAskSseEventAsync("error", new AskStreamErrorEvent("Generation service unavailable.", "service_unavailable"), CancellationToken.None);
        }
        catch (OllamaResponseException ex)
        {
            _logger.LogWarning(ex, "Generation response invalid for /ask/stream request.");
            await WriteAskSseEventAsync("error", new AskStreamErrorEvent("Generation service returned invalid response.", "bad_response"), CancellationToken.None);
        }
    }

    /// <summary>
    /// Gets configured generation models and default model for chat selection.
    /// </summary>
    /// <returns>Default model and allowed model list.</returns>
    /// <response code="200">Model configuration returned.</response>
    [HttpGet("models")]
    public IActionResult GetModels()
    {
        var ragOptions = _options.Value;
        var availableModels = GetAvailableGenerationModels(ragOptions);
        var defaultModel = ResolveDefaultModel(ragOptions, availableModels);
        return Ok(new { defaultModel, models = availableModels });
    }

    /// <summary>
    /// Clears all persisted vector records from the RAG store.
    /// </summary>
    /// <returns>Count of removed records.</returns>
    /// <response code="200">Vector store cleared successfully.</response>
    [HttpDelete("data")]
    public async Task<IActionResult> ClearData(CancellationToken ct)
    {
        var removed = await _vectorStore.ClearAsync(ct);
        return Ok(new { message = "RAG data cleared.", removed });
    }

    /// <summary>
    /// Gets the number of vectorized records currently stored in the RAG store.
    /// </summary>
    /// <returns>Total stored record count.</returns>
    /// <response code="200">Current record count returned.</response>
    [HttpGet("data/count")]
    public async Task<IActionResult> GetDataCount(CancellationToken ct)
    {
        var totalStored = await _vectorStore.CountAsync(ct);
        return Ok(new { totalStored });
    }

    private static string GetSafeFileDisplay(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return string.IsNullOrWhiteSpace(relative) ? Path.GetFileName(path) : relative;
    }

    private static IReadOnlyList<string> GetAvailableGenerationModels(RagOptions options)
    {
        var configured = options.GenerationModels
            ?.Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();

        if (configured.Count == 0 && !string.IsNullOrWhiteSpace(options.GenerationModel))
        {
            configured.Add(options.GenerationModel.Trim());
        }

        return configured;
    }

    private static string ResolveDefaultModel(RagOptions options, IReadOnlyList<string> availableModels)
    {
        if (!string.IsNullOrWhiteSpace(options.GenerationModel))
        {
            var configuredDefault = options.GenerationModel.Trim();
            var match = availableModels.FirstOrDefault(m => string.Equals(m, configuredDefault, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }

            return configuredDefault;
        }

        return availableModels.FirstOrDefault() ?? string.Empty;
    }

    private static IEnumerable<string> SplitIntoChunks(string text, int maxChars = 1000, int overlap = 200)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars));
        if (overlap < 0) overlap = 0;
        if (overlap >= maxChars) overlap = Math.Max(0, maxChars - 1);

        int start = 0;
        var length = text.Length;

        while (start < length)
        {
            var remaining = length - start;
            var take = Math.Min(maxChars, remaining);

            if (take == maxChars && start + take < length)
            {
                var slice = text.Substring(start, take);
                var lastNewline = slice.LastIndexOf('\n');
                var lastPeriod = slice.LastIndexOf(". ", StringComparison.Ordinal);
                var cut = Math.Max(lastNewline, lastPeriod);
                if (cut > 0)
                {
                    take = cut + (slice[cut] == '\n' ? 1 : 2);
                }
            }

            yield return text.Substring(start, take);

            if (start + take >= length) break;

            start = Math.Max(0, start + take - overlap);
        }
    }

    private async Task<IngestResponse> RunIngestAsync(string operationId, Func<IngestProgressEvent, Task>? progressCallback, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ragOptions = _options.Value;
        var maxParallelFiles = ClampConcurrency(ragOptions.IngestMaxParallelFiles, defaultValue: 2);

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".txt", ".cs", ".js", ".ts", ".json", ".html", ".razor"
        };

        var configuredRoot = ragOptions.KnowledgeBasePath;
        var root = Path.IsPathFullyQualified(configuredRoot)
            ? configuredRoot
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configuredRoot));

        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException("Knowledge base directory not found.");
        }

        var files = EnumerateIngestFiles(root, allowedExtensions).ToArray();
        var totalFiles = files.Length;
        var failures = new ConcurrentBag<IngestFailure>();
        var indexedFiles = 0;
        var chunksAdded = 0;
        var completedFiles = 0;

        if (progressCallback is not null)
        {
            await progressCallback(new IngestProgressEvent(
                "started",
                totalFiles,
                0,
                totalFiles,
                totalFiles == 0 ? 100 : 0,
                operationId));
        }

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = maxParallelFiles
            },
            async (file, token) =>
            {
                var fileResult = await ProcessFileAsync(
                    file,
                    root,
                    ragOptions,
                    token);

                if (fileResult.Indexed)
                {
                    Interlocked.Increment(ref indexedFiles);
                    Interlocked.Add(ref chunksAdded, fileResult.ChunksAdded);
                }

                if (fileResult.Failure is not null)
                {
                    failures.Add(fileResult.Failure);
                }

                var completed = Interlocked.Increment(ref completedFiles);
                if (progressCallback is not null)
                {
                    var remaining = Math.Max(0, totalFiles - completed);
                    var percent = totalFiles == 0 ? 100 : (int)Math.Round((completed * 100d) / totalFiles);
                    await progressCallback(new IngestProgressEvent(
                        fileResult.Status,
                        totalFiles,
                        completed,
                        remaining,
                        percent,
                        operationId,
                        fileResult.DisplayFile));
                }
            });

        sw.Stop();

        var totalStored = await _vectorStore.CountAsync(ct);

        return new IngestResponse(
            "Ingestion complete",
            totalFiles,
            indexedFiles,
            chunksAdded,
            failures.ToArray(),
            (int)Math.Min(int.MaxValue, totalStored),
            sw.ElapsedMilliseconds);
    }

    private async Task<IngestFileResult> ProcessFileAsync(
        string file,
        string root,
        RagOptions ragOptions,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var displayFile = GetSafeFileDisplay(root, file);
        var status = "indexed";

        try
        {
            if (ragOptions.MaxIngestFileBytes > 0)
            {
                var fileSize = new FileInfo(file).Length;
                if (fileSize > ragOptions.MaxIngestFileBytes)
                {
                    status = "skipped";
                    return new IngestFileResult(
                        displayFile,
                        status,
                        Indexed: false,
                        ChunksAdded: 0,
                        Failure: new IngestFailure(displayFile, "file_too_large", $"File exceeds max size of {ragOptions.MaxIngestFileBytes} bytes."));
                }
            }

            var text = await System.IO.File.ReadAllTextAsync(file, ct);
            if (ragOptions.MaxIngestFileChars > 0 && text.Length > ragOptions.MaxIngestFileChars)
            {
                text = text[..ragOptions.MaxIngestFileChars];
            }

            text = text.Replace("\r\n", "\n").Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return new IngestFileResult(displayFile, "skipped", Indexed: false, ChunksAdded: 0, Failure: null);
            }

            var collection = _vectorStore.ResolveCollection(root, file);
            await _vectorStore.DeleteBySourceAsync(collection, file, ct);

            var batchSize = Math.Clamp(ragOptions.QdrantUpsertBatchSize, 1, 256);
            var batch = new List<DocumentRecord>(batchSize);
            var chunkIndex = 0;
            var chunksAdded = 0;

            foreach (var chunk in SplitIntoChunks(text, ragOptions.ChunkSizeChars, ragOptions.ChunkOverlapChars))
            {
                ct.ThrowIfCancellationRequested();
                var normalizedChunk = EmbeddingService.NormalizeForEmbedding(chunk);
                if (string.IsNullOrWhiteSpace(normalizedChunk))
                {
                    chunkIndex++;
                    continue;
                }

                var embedding = await _embeddingService.EmbedAsync(normalizedChunk, ct);
                batch.Add(new DocumentRecord
                {
                    Id = BuildPointId(file, chunkIndex),
                    Source = file,
                    ChunkIndex = chunkIndex,
                    Text = chunk,
                    Embedding = embedding
                });

                chunkIndex++;

                if (batch.Count >= batchSize)
                {
                    await _vectorStore.UpsertAsync(collection, batch, ct);
                    chunksAdded += batch.Count;
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await _vectorStore.UpsertAsync(collection, batch, ct);
                chunksAdded += batch.Count;
            }

            if (chunksAdded == 0)
            {
                return new IngestFileResult(displayFile, "skipped", Indexed: false, ChunksAdded: 0, Failure: null);
            }

            return new IngestFileResult(displayFile, status, Indexed: true, ChunksAdded: chunksAdded, Failure: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OllamaTimeoutException ex)
        {
            _logger.LogWarning(ex, "Embedding timeout for {File}", displayFile);
            return new IngestFileResult(displayFile, "failed", Indexed: false, ChunksAdded: 0, Failure: new IngestFailure(displayFile, "embedding_timeout", "Embedding request timed out."));
        }
        catch (OllamaRequestException ex)
        {
            _logger.LogWarning(ex, "Embedding request failed for {File}", displayFile);
            return new IngestFileResult(displayFile, "failed", Indexed: false, ChunksAdded: 0, Failure: new IngestFailure(displayFile, "embedding_unavailable", "Embedding service unavailable."));
        }
        catch (OllamaResponseException ex)
        {
            _logger.LogWarning(ex, "Embedding response invalid for {File}", displayFile);
            return new IngestFileResult(displayFile, "failed", Indexed: false, ChunksAdded: 0, Failure: new IngestFailure(displayFile, "embedding_bad_response", "Embedding service returned an invalid response."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ingest failed for {File}", displayFile);
            return new IngestFileResult(displayFile, "failed", Indexed: false, ChunksAdded: 0, Failure: new IngestFailure(displayFile, "ingest_failed", "Failed to ingest file."));
        }
    }

    private static int ClampConcurrency(int configuredValue, int defaultValue)
    {
        var value = configuredValue <= 0 ? defaultValue : configuredValue;
        return Math.Clamp(value, 1, 8);
    }

    private static string BuildPointId(string source, int chunkIndex)
    {
        var input = $"{source}::chunk-{chunkIndex}";
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash).ToString("D");
    }

    private async Task WriteSseEventAsync(string eventName, IngestProgressEvent payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await Response.WriteAsync($"event: {eventName}\n", ct);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    private static IEnumerable<string> EnumerateIngestFiles(string root, IReadOnlySet<string> allowedExtensions)
    {
        return EnumerateIngestFilesCore(root, allowedExtensions);
    }

    private static IEnumerable<string> EnumerateIngestFilesCore(string currentDirectory, IReadOnlySet<string> allowedExtensions)
    {
        foreach (var file in Directory.EnumerateFiles(currentDirectory, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (allowedExtensions.Contains(Path.GetExtension(file)))
            {
                yield return file;
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(currentDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var directoryName = Path.GetFileName(directory);
            if (directoryName.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var file in EnumerateIngestFilesCore(directory, allowedExtensions))
            {
                yield return file;
            }
        }
    }

    private async Task WriteAskSseEventAsync<T>(string eventName, T payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await Response.WriteAsync($"event: {eventName}\n", ct);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    private static IReadOnlyList<ChatTurn> NormalizeHistory(IReadOnlyList<ChatTurn>? history)
    {
        if (history is null || history.Count == 0)
        {
            return Array.Empty<ChatTurn>();
        }

        return history
            .Where(turn => turn is not null)
            .Select(turn =>
            {
                var role = string.Equals(turn.Role?.Trim(), "assistant", StringComparison.OrdinalIgnoreCase)
                    ? "assistant"
                    : "user";
                var content = turn.Content?.Trim() ?? string.Empty;
                return new ChatTurn(role, content);
            })
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Content))
            .TakeLast(MaxHistoryItems)
            .ToArray();
    }

    private sealed record IngestFileResult(
        string DisplayFile,
        string Status,
        bool Indexed,
        int ChunksAdded,
        IngestFailure? Failure);
}
