using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Diagnostics;

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
            return BadRequest(new ErrorResponse("knowledge_base_not_found", "Knowledge base directory not found."));
        }

        var files = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => allowedExtensions.Contains(Path.GetExtension(f)))
            .ToArray();

        var failures = new List<IngestFailure>();
        var indexedFiles = 0;
        var chunksAdded = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var displayFile = GetSafeFileDisplay(root, file);

            try
            {
                var text = await System.IO.File.ReadAllTextAsync(file, ct);
                text = text.Replace("\r\n", "\n").Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
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
                _logger.LogWarning(ex, "Embedding timeout for {File}", displayFile);
                failures.Add(new IngestFailure(displayFile, "embedding_timeout", "Embedding request timed out."));
            }
            catch (OllamaRequestException ex)
            {
                _logger.LogWarning(ex, "Embedding request failed for {File}", displayFile);
                failures.Add(new IngestFailure(displayFile, "embedding_unavailable", "Embedding service unavailable."));
            }
            catch (OllamaResponseException ex)
            {
                _logger.LogWarning(ex, "Embedding response invalid for {File}", displayFile);
                failures.Add(new IngestFailure(displayFile, "embedding_bad_response", "Embedding service returned an invalid response."));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ingest failed for {File}", displayFile);
                failures.Add(new IngestFailure(displayFile, "ingest_failed", "Failed to ingest file."));
            }
        }

        _vectorStore.Save();
        sw.Stop();

        return Ok(new IngestResponse(
            "Ingestion complete",
            files.Length,
            indexedFiles,
            chunksAdded,
            failures,
            _vectorStore.Documents.Count,
            sw.ElapsedMilliseconds));
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

        try
        {
            var result = await _ragEngine.AskAsync(req.Query, ct);
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
}
