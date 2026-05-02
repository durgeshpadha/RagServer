using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;

namespace RagServer.API.Controllers;

[ApiController]
[Route("")]
public class RagController : ControllerBase
{
    private readonly EmbeddingService _embeddingService;
    private readonly VectorStore _vectorStore;
    private readonly RagEngine _ragEngine;
    private readonly IOptions<RagOptions> _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<RagController> _logger;

    public RagController(
        EmbeddingService embeddingService,
        VectorStore vectorStore,
        RagEngine ragEngine,
        IOptions<RagOptions> options,
        IWebHostEnvironment environment,
        ILogger<RagController> logger)
    {
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _ragEngine = ragEngine;
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
        try
        {
            var result = await RunIngestAsync(progressCallback: null, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Knowledge base directory not found.", StringComparison.Ordinal))
        {
            return BadRequest(new ErrorResponse("knowledge_base_not_found", "Knowledge base directory not found."));
        }
    }

    [HttpPost("ingest/stream")]
    public async Task IngestStream(CancellationToken ct)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("X-Accel-Buffering", "no");

        try
        {
            var summary = await RunIngestAsync(async progress =>
            {
                await WriteSseEventAsync("progress", progress, ct);
            }, ct);

            var completed = new IngestProgressEvent(
                "completed",
                summary.FilesScanned,
                summary.FilesScanned,
                0,
                100,
                Summary: summary);

            await WriteSseEventAsync("completed", completed, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected/cancelled; no extra write needed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error during ingest stream.");
            var errorEvent = new IngestProgressEvent("error", 0, 0, 0, 0, ErrorMessage: ex.Message);
            await WriteSseEventAsync("error", errorEvent, CancellationToken.None);
        }
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

        try
        {
            var result = req.UseKnowledgeBase
                ? await _ragEngine.AskWithKnowledgeBaseAsync(req.Query, selectedModel, ct)
                : await _ragEngine.AskDirectAsync(req.Query, selectedModel, ct);
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
    public IActionResult ClearData()
    {
        var removed = _vectorStore.Clear();
        _vectorStore.Save();
        return Ok(new { message = "RAG data cleared.", removed });
    }

    /// <summary>
    /// Gets the number of vectorized records currently stored in the RAG store.
    /// </summary>
    /// <returns>Total stored record count.</returns>
    /// <response code="200">Current record count returned.</response>
    [HttpGet("data/count")]
    public IActionResult GetDataCount()
    {
        return Ok(new { totalStored = _vectorStore.Documents.Count });
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

    private async Task<IngestResponse> RunIngestAsync(Func<IngestProgressEvent, Task>? progressCallback, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ragOptions = _options.Value;

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".md", ".txt", ".cs", ".js", ".ts", ".json", ".yml", ".yaml", ".html", ".razor"
        };

        var configuredRoot = ragOptions.KnowledgeBasePath;
        var root = Path.IsPathFullyQualified(configuredRoot)
            ? configuredRoot
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configuredRoot));

        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException("Knowledge base directory not found.");
        }

        var files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => allowedExtensions.Contains(Path.GetExtension(f)))
            .ToArray();

        var failures = new List<IngestFailure>();
        var indexedFiles = 0;
        var chunksAdded = 0;
        var completedFiles = 0;
        var totalFiles = files.Length;

        if (progressCallback is not null)
        {
            await progressCallback(new IngestProgressEvent(
                "started",
                totalFiles,
                0,
                totalFiles,
                totalFiles == 0 ? 100 : 0));
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var displayFile = GetSafeFileDisplay(root, file);
            var status = "indexed";

            try
            {
                var text = await System.IO.File.ReadAllTextAsync(file, ct);
                text = text.Replace("\r\n", "\n").Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    status = "skipped";
                    continue;
                }

                var chunks = SplitIntoChunks(text, ragOptions.ChunkSizeChars, ragOptions.ChunkOverlapChars).ToArray();
                var newRecords = new List<DocumentRecord>(chunks.Length);

                for (int i = 0; i < chunks.Length; i++)
                {
                    var normalizedChunk = EmbeddingService.NormalizeForEmbedding(chunks[i]);
                    if (string.IsNullOrWhiteSpace(normalizedChunk))
                    {
                        continue;
                    }

                    var embedding = await _embeddingService.EmbedAsync(normalizedChunk, ct);
                    newRecords.Add(new DocumentRecord
                    {
                        Id = $"{file}::chunk-{i}",
                        Source = file,
                        ChunkIndex = i,
                        Text = chunks[i],
                        Embedding = embedding
                    });
                }

                _vectorStore.RemoveBySource(file);
                _vectorStore.AddRange(newRecords);

                indexedFiles++;
                chunksAdded += newRecords.Count;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OllamaTimeoutException ex)
            {
                status = "failed";
                _logger.LogWarning(ex, "Embedding timeout for {File}", displayFile);
                failures.Add(new IngestFailure(displayFile, "embedding_timeout", "Embedding request timed out."));
            }
            catch (OllamaRequestException ex)
            {
                status = "failed";
                _logger.LogWarning(ex, "Embedding request failed for {File}", displayFile);
                failures.Add(new IngestFailure(displayFile, "embedding_unavailable", "Embedding service unavailable."));
            }
            catch (OllamaResponseException ex)
            {
                status = "failed";
                _logger.LogWarning(ex, "Embedding response invalid for {File}", displayFile);
                failures.Add(new IngestFailure(displayFile, "embedding_bad_response", "Embedding service returned an invalid response."));
            }
            catch (Exception ex)
            {
                status = "failed";
                _logger.LogWarning(ex, "Ingest failed for {File}", displayFile);
                failures.Add(new IngestFailure(displayFile, "ingest_failed", "Failed to ingest file."));
            }
            finally
            {
                completedFiles++;
                if (progressCallback is not null)
                {
                    var remaining = Math.Max(0, totalFiles - completedFiles);
                    var percent = totalFiles == 0 ? 100 : (int)Math.Round((completedFiles * 100d) / totalFiles);
                    await progressCallback(new IngestProgressEvent(
                        status,
                        totalFiles,
                        completedFiles,
                        remaining,
                        percent,
                        displayFile));
                }
            }
        }

        _vectorStore.Save();
        sw.Stop();

        return new IngestResponse(
            "Ingestion complete",
            files.Length,
            indexedFiles,
            chunksAdded,
            failures,
            _vectorStore.Documents.Count,
            sw.ElapsedMilliseconds);
    }

    private async Task WriteSseEventAsync(string eventName, IngestProgressEvent payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await Response.WriteAsync($"event: {eventName}\n", ct);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}
