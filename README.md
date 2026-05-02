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

## Recreate `RAG-KnowledgeBase` Repos

Use this PowerShell script to recreate the same nested folder structure and clone all repos into `RAG-KnowledgeBase`:

```powershell
$root = "RAG-KnowledgeBase"

$repos = @(
    @{ Path = "dotnet\api\dotnet-api-docs"; Url = "https://github.com/dotnet/dotnet-api-docs" },
    @{ Path = "dotnet\aspnetcore\AspNetCore.Docs"; Url = "https://github.com/dotnet/AspNetCore.Docs" },
    @{ Path = "dotnet\core"; Url = "https://github.com/dotnet/core" },
    @{ Path = "dotnet\csharp\csharplang"; Url = "https://github.com/dotnet/csharplang" },
    @{ Path = "dotnet\efcore\EntityFramework.Docs"; Url = "https://github.com/dotnet/EntityFramework.Docs" },
    @{ Path = "javascript\jquery\api.jquery.com\jquery"; Url = "https://github.com/jquery/jquery" },
    @{ Path = "javascript\mdn\content"; Url = "https://github.com/mdn/content" },
    @{ Path = "react\react.dev"; Url = "https://github.com/reactjs/react.dev" }
)

New-Item -ItemType Directory -Force -Path $root | Out-Null

foreach ($repo in $repos) {
    $target = Join-Path $root $repo.Path
    $parent = Split-Path -Parent $target
    New-Item -ItemType Directory -Force -Path $parent | Out-Null

    if (-not (Test-Path $target)) {
        git clone $repo.Url $target
    }
    else {
        Write-Host "Skipping existing repo: $target"
    }
}
```

## Prerequisites

- .NET SDK 10
- Qdrant (vector database) running at `http://localhost:6333`
- Ollama (or compatible endpoint) running for embeddings/generation

### Qdrant via Docker Compose

This repo includes [`docker-compose.yml`] for Qdrant.

Start Qdrant:

```bash
docker compose up -d
```

Verify it is running:

```bash
docker compose ps
```

Qdrant endpoints:

- `http://localhost:6333` (HTTP API)
- `http://localhost:6334` (gRPC)

Stop Qdrant:

```bash
docker compose down
```

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
