# RagServer.Api

Minimal-dependency RAG API in C# using local files + Ollama.

## Endpoints

- `POST /ingest`: reads files from `Rag:KnowledgeBasePath`, chunks, embeds, and stores vectors.
- `POST /ingest/stream`: streams ingest progress events (SSE) and final summary.
- `POST /ask`: answers with or without retrieval depending on `useKnowledgeBase`.
- `POST /ask/stream`: streams token chunks (SSE) and emits final answer + citations.
- `GET /models`: returns configured chat models and default model.
- `DELETE /data`: clears all indexed vector data.

## Prerequisites

- .NET SDK 10
- Ollama running locally (`http://localhost:11434` by default)
- Models available in Ollama:
  - embedding: `nomic-embed-text`
  - generation (example): `deepseek-coder-v2:16b`, `qwen2.5-coder:14b`, `gemma4:e2b`

## Configuration (`appsettings.json`)

`Rag` section:

- `KnowledgeBasePath`: source folder for ingestion.
- `VectorStorePath`: JSON file path for persisted vectors.
- `OllamaBaseUrl`: Ollama base URL.
- `EmbeddingModel`: model used for embeddings.
- `GenerationModel`: default model used for answers (fallback/backward compatibility).
- `GenerationModels`: allowed models for chat selection.
- `MaxQueryChars`: query validation limit.
- `TopK`: retrieval count.
- `MaxContextChars`: max context characters sent to generation model.
- `ChunkSizeChars`: chunk size for ingestion.
- `ChunkOverlapChars`: overlap between chunks.

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

Use [RagServer.Api.http](./RagServer.Api.http) for ready-to-run requests.
