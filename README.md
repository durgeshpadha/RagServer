# RagServer

RagServer is a .NET 10 solution with:

- `RagServer.API`: RAG backend API (ingest, ask, data management)
- `RagServer.API.Tests`: API test project
- `RagServer.Web`: Blazor WebAssembly frontend

## Project Structure

- `RagServer.API/` - ASP.NET Core API with Swagger and ReDoc
- `RagServer.API.Tests/` - xUnit tests for API behavior
- `RagServer.Web/` - Blazor standalone UI (chatbot + ingest + data controls)
- `RagServer.slnx` - solution file

## Prerequisites

- .NET SDK 10
- Ollama (or compatible endpoint) running for embeddings/generation

### Ollama Models

Default models configured in `RagServer.API`:

- Embedding model: `nomic-embed-text`
- Generation model: `deepseek-coder-v2`

Pull them before running the API:

```bash
ollama pull nomic-embed-text
ollama pull deepseek-coder-v2
```

Start Ollama (if not already running) and verify:

```bash
ollama list
```

## Run API

```bash
dotnet run --project RagServer.API/RagServer.API.csproj
```

API docs:

- Swagger: `http://localhost:5228/swagger`
- ReDoc: `http://localhost:5228/redoc`

## Run Web UI

```bash
dotnet run --project RagServer.Web/RagServer.Web.csproj
```

By default, `RagServer.Web` calls the API at:

- `http://localhost:5228/`

You can change this in:

- `RagServer.Web/wwwroot/appsettings.json`

## Run Tests

```bash
dotnet test RagServer.API.Tests/RagServer.API.Tests.csproj
```

## Features

- Ingest knowledge-base files into vector store
- Ask questions via chatbot UI backed by RAG
- View stored data count
- Clear stored RAG data
