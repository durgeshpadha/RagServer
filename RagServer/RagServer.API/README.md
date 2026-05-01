# RagServer.Api

Minimal-dependency RAG API in C# using local files + Ollama.

## Endpoints

- `POST /ingest`: reads files from `Rag:KnowledgeBasePath`, chunks, embeds, and stores vectors.
- `POST /ask`: embeds query, retrieves top matches, and generates an answer with citations.
- `DELETE /data`: clears all indexed vector data.

## Prerequisites

- .NET SDK 10
- Ollama running locally (`http://localhost:11434` by default)
- Models available in Ollama:
  - embedding: `nomic-embed-text`
  - generation: `deepseek-coder-v2`

## Configuration (`appsettings.json`)

`Rag` section:

- `KnowledgeBasePath`: source folder for ingestion.
- `VectorStorePath`: JSON file path for persisted vectors.
- `OllamaBaseUrl`: Ollama base URL.
- `EmbeddingModel`: model used for embeddings.
- `GenerationModel`: model used for answers.
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
- Absolute file paths are not returned in ingest failure payloads.

## Manual test flow

1. Run `POST /ingest`
2. Run `POST /ask`
3. Run `DELETE /data`
4. Run `POST /ask` again (should report no relevant data)

Use [RagServer.Api.http](./RagServer.Api.http) for ready-to-run requests.
