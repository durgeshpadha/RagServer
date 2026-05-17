# RagServer.Api

Minimal-dependency RAG API in C# using local files + Ollama.

## Endpoints

- `POST /ingest`: reads files from `Rag:KnowledgeBasePath`, chunks, embeds, and stores vectors.
- `POST /ingest/stream`: streams ingest progress events (SSE) and final summary.
- `POST /ingest/start`: creates a new ingest operation and returns `operationId`.
- `GET /ingest/{operationId}/stream`: streams progress for a started operation.
- `POST /ingest/{operationId}/cancel`: requests server-side cancellation.
- `POST /ask`: answers with or without retrieval depending on `useKnowledgeBase`.
- `POST /ask/stream`: streams token chunks (SSE) and emits final answer + citations.
- `GET /models`: returns configured chat models and default model.
- `DELETE /data`: clears all indexed vector data.
- `GET /data/count`: returns total vector records currently stored.

## Endpoint Usage In UI (`RagServer.Web`)

### In use

1. `POST /ask/stream`
   - Used for chat response streaming.
   - Emits `token`, `completed`, and `error` SSE events.
2. `POST /ingest/start`
   - Starts ingest and returns `operationId`.
3. `GET /ingest/{operationId}/stream`
   - Streams ingest progress for the started operation.
4. `POST /ingest/{operationId}/cancel`
   - Cancels active ingest when user clicks stop.
5. `GET /models`
   - Loads model list/default for UI selector.
6. `GET /data/count`
   - Fetches stored chunk count for dashboard.
7. `DELETE /data`
   - Clears stored vectors from UI action.

### Not in use

1. `POST /ingest`
   - Full synchronous ingest endpoint (UI uses start+stream flow instead).
2. `POST /ingest/stream`
   - Combined start+stream endpoint (UI uses explicit two-step flow).
3. `POST /ask`
   - Non-streaming ask endpoint (UI uses streaming ask endpoint).

## Prerequisites

- .NET SDK 10
- Ollama running locally (`http://localhost:11434` by default)
- Models available in Ollama:
  - embedding: `nomic-embed-text`
  - generation (example): `deepseek-coder-v2:16b`, `qwen2.5-coder:14b`, `gemma4:e2b`

## Configuration (`appsettings.json`)

`Rag` section:

- `KnowledgeBasePath`: source folder for ingestion.
- `QdrantUrl`: Qdrant base URL (default `http://localhost:6333`).
- `QdrantCollectionPrefix`: prefix used for folder-wise collections (`rag_` by default).
- `CollectionBuckets`: top-level folder buckets mapped to collections (`dotnet`, `javascript`, `react`).
- `QdrantDistance`: vector distance metric for created collections (`Cosine` by default).
- `QdrantUpsertBatchSize`: chunk batch size for upserts.
- `QdrantTimeoutSeconds`: timeout for Qdrant requests.
- `VectorStorePath`: legacy JSON path (kept for backward compatibility; not used in Qdrant mode).
- `OllamaBaseUrl`: Ollama base URL.
- `EmbeddingModel`: model used for embeddings.
- `GenerationModel`: default model used for answers (fallback/backward compatibility).
- `GenerationModels`: allowed models for chat selection.
- `MaxQueryChars`: query validation limit.
- `TopK`: retrieval count.
- `MaxContextChars`: max context characters sent to generation model.
- `ChunkSizeChars`: chunk size for ingestion.
- `ChunkOverlapChars`: overlap between chunks.
- `IngestMaxParallelFiles`: max files processed concurrently during ingest (clamped to `1..8`).
- `IngestMaxParallelEmbeddingsPerFile`: max concurrent embedding calls per file (clamped to `1..8`).
- `MaxIngestFileBytes`: skip files larger than this byte size (`0` disables limit).
- `MaxIngestFileChars`: trim file text to this many chars before chunking (`0` disables limit).
- Ingest discovery excludes directories whose name starts with `.` (recursive).
- Ingest discovery excludes `.yaml` and `.yml` files.

Recommended starting values for laptops: `IngestMaxParallelFiles = 2`, `IngestMaxParallelEmbeddingsPerFile = 2`.

## Reliability behavior

- Corrupted vector file will not prevent startup (fallback to empty in-memory store, warning logged).
- Ingestion is atomic per file (old chunks replaced only after new chunks are fully prepared).
- Ollama failures are mapped to safe status codes in `/ask`:
  - `503` service unavailable
  - `504` timeout
  - `502` invalid upstream response
- `/ask/stream` emits SSE `error` events for generation failures after stream start.
- Invalid model selection in `/ask` returns `400` with error code `invalid_model`.
- `/ask` supports `useKnowledgeBase: false` for direct model answers (no citations).
- `/ask` and `/ask/stream` support optional `history` for multi-turn context.
- Absolute file paths are not returned in ingest failure payloads.

## Manual test flow

1. Run `POST /ingest/stream` and watch progress events
2. Run `GET /models`
3. Run `POST /ask` with `useKnowledgeBase: true`
4. Run `POST /ask` with `useKnowledgeBase: false`
5. Run `POST /ask/stream` and verify token events + final completed event
6. Run `POST /ask` with unsupported model (expect `400 invalid_model`)
7. Run `DELETE /data`
8. Run `POST /ask` again (RAG mode should report no relevant data)

Use [RagServer.API.http](./RagServer.API.http) for ready-to-run requests.
