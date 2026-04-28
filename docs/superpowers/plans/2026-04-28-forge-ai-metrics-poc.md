# Forge AI Metrics POC Implementation Plan (.NET 10 + EF Core)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an end-to-end POC of the Forge AI Metrics platform from `2026-04-28-forge-ai-metrics-architecture.md`: dockerized MS SQL + ASP.NET Core 10 backend (ingest + metrics + setup-script endpoints + Swagger UI), an idempotent developer setup script, and a reproducible local onboarding flow that ingests git-ai notes from real local commits.

**Architecture:** ASP.NET Core 10 minimal APIs expose `/api/ingest`, `/api/metrics/*`, and `/setup/{teamId}/{apiKey}`. EF Core 10 (`Microsoft.EntityFrameworkCore.SqlServer`) talks to MS SQL Server 2022 in Docker. Schema and a seeded test team are bootstrapped on app startup via `EnsureCreatedAsync()` with a connect-retry loop. Swagger UI is served at `/swagger` by `Swashbuckle.AspNetCore`. The whole stack runs via `docker compose up`. The plan finishes by onboarding the developer's own machine and pushing to GitHub.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core 10, `Microsoft.EntityFrameworkCore.SqlServer`, `Swashbuckle.AspNetCore`, xUnit, MS SQL Server 2022 (Docker), Docker Compose, bash, curl, jq.

**Reference document:** `2026-04-28-forge-ai-metrics-architecture.md` — referenced throughout as "spec §N".

**Target repo:** https://github.com/ShamsiievDmytro/metrics-architecture

---

## File Layout

```
metrics-architecture/
├── 2026-04-28-forge-ai-metrics-architecture.md   (existing)
├── README.md
├── .gitignore
├── .env.example
├── docker-compose.yml
├── Makefile
├── api/
│   ├── Dockerfile
│   ├── Forge.Metrics.sln
│   ├── Forge.Metrics.Api/
│   │   ├── Forge.Metrics.Api.csproj
│   │   ├── Program.cs                # builder + endpoints + bootstrap
│   │   ├── appsettings.json
│   │   ├── appsettings.Development.json
│   │   ├── Configuration/
│   │   │   └── ForgeOptions.cs
│   │   ├── Data/
│   │   │   ├── ForgeDbContext.cs
│   │   │   ├── Team.cs
│   │   │   └── Commit.cs
│   │   ├── Auth/
│   │   │   ├── ApiKeyFilter.cs
│   │   │   └── ApiKeyHasher.cs
│   │   ├── Parsing/
│   │   │   ├── NoteParser.cs
│   │   │   ├── ParsedNote.cs
│   │   │   └── AttributionCalculator.cs
│   │   ├── Endpoints/
│   │   │   ├── IngestEndpoints.cs
│   │   │   ├── MetricsEndpoints.cs
│   │   │   └── SetupEndpoints.cs
│   │   └── Dtos/
│   │       ├── IngestPayload.cs
│   │       ├── IngestResponse.cs
│   │       └── MetricsDtos.cs
│   └── Forge.Metrics.Tests/
│       ├── Forge.Metrics.Tests.csproj
│       ├── NoteParserTests.cs
│       └── AttributionCalculatorTests.cs
├── db/
│   └── 001_schema.sql                 # reference; runtime uses EnsureCreated
├── scripts/
│   ├── enrich-and-post.sh             # served verbatim from /setup
│   └── setup.sh.tmpl                  # rendered with teamId + apiKey
└── docs/
    └── superpowers/
        └── plans/
            └── 2026-04-28-forge-ai-metrics-poc.md   (this file)
```

**Responsibility split rationale:**
- `Parsing/` is pure C# (no EF/HTTP) — easy to unit test.
- `Data/` owns the DbContext + entities; EF config lives in `OnModelCreating`.
- `Endpoints/` are thin minimal-API extension methods so `Program.cs` stays small.
- `Auth/` is the API-key endpoint filter; reused by ingest + metrics groups.
- `Dtos/` are records — request/response shapes only.

---

## Task 1: Repo skeleton, .gitignore, README stub

**Files:**
- Create: `/Users/dmytroshamsiiev/Projects/metrics-architecture/.gitignore`
- Create: `/Users/dmytroshamsiiev/Projects/metrics-architecture/README.md`
- Create: `/Users/dmytroshamsiiev/Projects/metrics-architecture/.env.example`

- [ ] **Step 1: Initialize git repo**

```bash
cd /Users/dmytroshamsiiev/Projects/metrics-architecture
git init -b main
```

Expected: `Initialized empty Git repository in .../metrics-architecture/.git/`

- [ ] **Step 2: Write `.gitignore`**

```gitignore
# .NET
bin/
obj/
*.user
*.suo
.vs/

# IDE
.idea/
.vscode/

# Env
.env
.env.local

# OS
.DS_Store

# Docker volumes (if mapped locally)
data/

# Test results
TestResults/
*.trx
coverage*.xml
```

- [ ] **Step 3: Write `.env.example`**

```dotenv
# MS SQL Server
MSSQL_SA_PASSWORD=ForgeAI!Strong#Pass1
MSSQL_PORT=1433

# API
API_PORT=8000
DB_NAME=forge_ai_metrics

# Public URL the setup script uses to POST back to the API
PUBLIC_API_URL=http://localhost:8000

# Seeded team (created on first boot if not present)
SEED_TEAM_NAME=Platform Team
SEED_TEAM_API_KEY=fai_dev_local_key_change_me
```

- [ ] **Step 4: Write `README.md` stub** (expanded in Task 19)

```markdown
# Forge AI Metrics — POC (.NET 10 + EF Core)

POC implementation of the architecture in `2026-04-28-forge-ai-metrics-architecture.md`.

## Quick start

```bash
cp .env.example .env
docker compose up -d --build
open http://localhost:8000/swagger
```

Onboarding (developer machine):

```bash
curl -s "http://localhost:8000/setup/<TEAM_ID>/<API_KEY>" | bash
```

See `2026-04-28-forge-ai-metrics-architecture.md` for the full architecture.
```

- [ ] **Step 5: Commit**

```bash
git add .gitignore README.md .env.example 2026-04-28-forge-ai-metrics-architecture.md docs/
git commit -m "chore: initial repo skeleton with architecture spec"
```

---

## Task 2: docker-compose with MS SQL + API services

**Files:**
- Create: `/Users/dmytroshamsiiev/Projects/metrics-architecture/docker-compose.yml`
- Create: `/Users/dmytroshamsiiev/Projects/metrics-architecture/Makefile`

- [ ] **Step 1: Write `docker-compose.yml`**

```yaml
services:
  mssql:
    image: mcr.microsoft.com/mssql/server:2022-latest
    platform: linux/amd64
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: ${MSSQL_SA_PASSWORD}
      MSSQL_PID: Developer
    ports:
      - "${MSSQL_PORT:-1433}:1433"
    healthcheck:
      test:
        - CMD-SHELL
        - /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$$MSSQL_SA_PASSWORD" -Q "SELECT 1" || exit 1
      interval: 10s
      timeout: 5s
      retries: 20
      start_period: 30s
    volumes:
      - mssql_data:/var/opt/mssql

  api:
    build: ./api
    depends_on:
      mssql:
        condition: service_healthy
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ASPNETCORE_URLS: http://+:8080
      ConnectionStrings__Default: "Server=mssql,1433;Database=${DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=True;"
      Forge__PublicApiUrl: ${PUBLIC_API_URL}
      Forge__SeedTeamName: ${SEED_TEAM_NAME}
      Forge__SeedTeamApiKey: ${SEED_TEAM_API_KEY}
      Forge__ScriptsPath: /scripts
      DOTNET_USE_POLLING_FILE_WATCHER: "1"
    ports:
      - "${API_PORT:-8000}:8080"
    volumes:
      - ./api:/src
      - ./scripts:/scripts:ro

volumes:
  mssql_data:
```

Notes:
- `platform: linux/amd64` is required for Apple Silicon — the official MS SQL image does not yet ship arm64 (Docker Desktop emulates via Rosetta).
- `DOTNET_USE_POLLING_FILE_WATCHER=1` makes `dotnet watch` reliable when source is bind-mounted.
- Host port `8000` → container port `8080` so external URLs in docs and the setup script stay stable.

- [ ] **Step 2: Write `Makefile`**

```makefile
.PHONY: up down logs restart api-shell db-shell test reseed

up:
	docker compose up -d --build

down:
	docker compose down

logs:
	docker compose logs -f api

restart:
	docker compose restart api

api-shell:
	docker compose exec api bash

db-shell:
	docker compose exec mssql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$$MSSQL_SA_PASSWORD" -d $(DB_NAME)

test:
	docker compose exec api dotnet test /src/Forge.Metrics.sln
```

- [ ] **Step 3: Commit**

```bash
git add docker-compose.yml Makefile
git commit -m "chore: docker compose stack with mssql + .net api"
```

---

## Task 3: Reference schema SQL

**Files:**
- Create: `db/001_schema.sql`

The runtime uses `EnsureCreatedAsync()` (Task 6) — this SQL is for review and manual psql sessions only.

- [ ] **Step 1: Write `db/001_schema.sql`**

```sql
-- Forge AI Metrics schema (spec §3). Idempotent — safe to re-run.
-- Reference only; runtime schema creation is handled by EF Core EnsureCreatedAsync().

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Teams')
BEGIN
  CREATE TABLE Teams (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    Name NVARCHAR(255) NOT NULL,
    ApiKeyHash NVARCHAR(255) NOT NULL UNIQUE,
    CreatedAt DATETIME2 DEFAULT GETUTCDATE()
  );
END;

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Commits')
BEGIN
  CREATE TABLE Commits (
    Id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
    TeamId UNIQUEIDENTIFIER NOT NULL REFERENCES Teams(Id),
    RepoName NVARCHAR(500) NOT NULL,
    RepoUrl NVARCHAR(1000),
    CommitSha NVARCHAR(40) NOT NULL,
    Branch NVARCHAR(500),
    IsDefaultBranch BIT DEFAULT 0,
    CommitAuthor NVARCHAR(255),
    Agent NVARCHAR(100),
    Model NVARCHAR(255),
    AgentLines INT NOT NULL DEFAULT 0,
    HumanLines INT NOT NULL DEFAULT 0,
    OverriddenLines INT NOT NULL DEFAULT 0,
    AgentPercentage DECIMAL(5,1) NOT NULL DEFAULT 0,
    DiffAdditions INT NOT NULL DEFAULT 0,
    DiffDeletions INT NOT NULL DEFAULT 0,
    CommittedAt DATETIME2,
    IngestedAt DATETIME2 DEFAULT GETUTCDATE(),
    RawNote NVARCHAR(MAX),
    CONSTRAINT UQ_Commit UNIQUE(TeamId, RepoName, CommitSha)
  );

  CREATE INDEX IX_Commits_Team ON Commits(TeamId);
  CREATE INDEX IX_Commits_Repo ON Commits(TeamId, RepoName);
  CREATE INDEX IX_Commits_Author ON Commits(TeamId, CommitAuthor);
  CREATE INDEX IX_Commits_Date ON Commits(TeamId, CommittedAt);
  CREATE INDEX IX_Commits_Agent ON Commits(TeamId, Agent);
END;
```

- [ ] **Step 2: Commit**

```bash
git add db/
git commit -m "docs(db): reference schema sql"
```

---

## Task 4: .NET solution skeleton

**Files:**
- Create: `api/Forge.Metrics.sln`
- Create: `api/Forge.Metrics.Api/Forge.Metrics.Api.csproj`
- Create: `api/Forge.Metrics.Api/appsettings.json`
- Create: `api/Forge.Metrics.Api/appsettings.Development.json`
- Create: `api/Forge.Metrics.Tests/Forge.Metrics.Tests.csproj`
- Create: `api/Dockerfile`

- [ ] **Step 1: Create folders**

```bash
mkdir -p /Users/dmytroshamsiiev/Projects/metrics-architecture/api/Forge.Metrics.Api/{Configuration,Data,Auth,Parsing,Endpoints,Dtos}
mkdir -p /Users/dmytroshamsiiev/Projects/metrics-architecture/api/Forge.Metrics.Tests
```

- [ ] **Step 2: Write `api/Forge.Metrics.Api/Forge.Metrics.Api.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
    <RootNamespace>Forge.Metrics.Api</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0" />
    <PackageReference Include="Swashbuckle.AspNetCore" Version="7.2.0" />
  </ItemGroup>
</Project>
```

If a newer compatible version of either package is published, use it — these are floors not pins.

- [ ] **Step 3: Write `api/Forge.Metrics.Api/appsettings.json`**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  },
  "ConnectionStrings": {
    "Default": "Server=mssql,1433;Database=forge_ai_metrics;User Id=sa;Password=ForgeAI!Strong#Pass1;TrustServerCertificate=True;"
  },
  "Forge": {
    "PublicApiUrl": "http://localhost:8000",
    "SeedTeamName": "Platform Team",
    "SeedTeamApiKey": "fai_dev_local_key_change_me",
    "ScriptsPath": "/scripts"
  }
}
```

- [ ] **Step 4: Write `api/Forge.Metrics.Api/appsettings.Development.json`**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.EntityFrameworkCore.Database.Command": "Information"
    }
  }
}
```

- [ ] **Step 5: Write `api/Forge.Metrics.Tests/Forge.Metrics.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <RootNamespace>Forge.Metrics.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Forge.Metrics.Api\Forge.Metrics.Api.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Write `api/Forge.Metrics.sln`**

Create with `dotnet`:

```bash
cd /Users/dmytroshamsiiev/Projects/metrics-architecture/api
dotnet new sln -n Forge.Metrics
dotnet sln add Forge.Metrics.Api/Forge.Metrics.Api.csproj
dotnet sln add Forge.Metrics.Tests/Forge.Metrics.Tests.csproj
```

If `dotnet` is not installed locally, write the `.sln` from inside the build container instead:

```bash
docker run --rm -v "$PWD:/work" -w /work mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -lc "dotnet new sln -n Forge.Metrics && \
            dotnet sln add Forge.Metrics.Api/Forge.Metrics.Api.csproj && \
            dotnet sln add Forge.Metrics.Tests/Forge.Metrics.Tests.csproj"
```

- [ ] **Step 7: Write `api/Dockerfile`**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /src

# Restore against full source so dotnet watch on bind-mounted source works.
COPY Forge.Metrics.sln ./
COPY Forge.Metrics.Api/Forge.Metrics.Api.csproj Forge.Metrics.Api/
COPY Forge.Metrics.Tests/Forge.Metrics.Tests.csproj Forge.Metrics.Tests/
RUN dotnet restore Forge.Metrics.sln

COPY . .

WORKDIR /src/Forge.Metrics.Api
EXPOSE 8080
CMD ["dotnet", "watch", "run", "--no-launch-profile", "--non-interactive", "--no-hot-reload"]
```

`--no-hot-reload` makes restarts deterministic for our local dev loop; turn it off if you prefer hot reload.

- [ ] **Step 8: Commit**

```bash
git add api/
git commit -m "feat(api): solution + projects + dockerfile"
```

---

## Task 5: Configuration options

**Files:**
- Create: `api/Forge.Metrics.Api/Configuration/ForgeOptions.cs`

- [ ] **Step 1: Write `Configuration/ForgeOptions.cs`**

```csharp
namespace Forge.Metrics.Api.Configuration;

public sealed class ForgeOptions
{
    public const string SectionName = "Forge";

    public string PublicApiUrl { get; init; } = "http://localhost:8000";
    public string SeedTeamName { get; init; } = "Platform Team";
    public string SeedTeamApiKey { get; init; } = "";
    public string ScriptsPath { get; init; } = "/scripts";
}
```

- [ ] **Step 2: Commit**

```bash
git add api/Forge.Metrics.Api/Configuration/
git commit -m "feat(api): ForgeOptions config binding"
```

---

## Task 6: EF Core entities, DbContext, ApiKeyHasher

**Files:**
- Create: `api/Forge.Metrics.Api/Data/Team.cs`
- Create: `api/Forge.Metrics.Api/Data/Commit.cs`
- Create: `api/Forge.Metrics.Api/Data/ForgeDbContext.cs`
- Create: `api/Forge.Metrics.Api/Auth/ApiKeyHasher.cs`

- [ ] **Step 1: Write `Data/Team.cs`**

```csharp
namespace Forge.Metrics.Api.Data;

public class Team
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ApiKeyHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 2: Write `Data/Commit.cs`**

```csharp
namespace Forge.Metrics.Api.Data;

public class Commit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TeamId { get; set; }
    public string RepoName { get; set; } = "";
    public string? RepoUrl { get; set; }
    public string CommitSha { get; set; } = "";
    public string? Branch { get; set; }
    public bool IsDefaultBranch { get; set; }
    public string? CommitAuthor { get; set; }
    public string? Agent { get; set; }
    public string? Model { get; set; }
    public int AgentLines { get; set; }
    public int HumanLines { get; set; }
    public int OverriddenLines { get; set; }
    public decimal AgentPercentage { get; set; }
    public int DiffAdditions { get; set; }
    public int DiffDeletions { get; set; }
    public DateTime? CommittedAt { get; set; }
    public DateTime IngestedAt { get; set; } = DateTime.UtcNow;
    public string? RawNote { get; set; }
}
```

- [ ] **Step 3: Write `Data/ForgeDbContext.cs`**

```csharp
using Microsoft.EntityFrameworkCore;

namespace Forge.Metrics.Api.Data;

public class ForgeDbContext(DbContextOptions<ForgeDbContext> options) : DbContext(options)
{
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Commit> Commits => Set<Commit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Team>(e =>
        {
            e.ToTable("Teams");
            e.HasKey(t => t.Id);
            e.Property(t => t.Name).HasMaxLength(255).IsRequired();
            e.Property(t => t.ApiKeyHash).HasMaxLength(255).IsRequired();
            e.HasIndex(t => t.ApiKeyHash).IsUnique();
        });

        modelBuilder.Entity<Commit>(e =>
        {
            e.ToTable("Commits");
            e.HasKey(c => c.Id);
            e.Property(c => c.RepoName).HasMaxLength(500).IsRequired();
            e.Property(c => c.RepoUrl).HasMaxLength(1000);
            e.Property(c => c.CommitSha).HasMaxLength(40).IsRequired();
            e.Property(c => c.Branch).HasMaxLength(500);
            e.Property(c => c.CommitAuthor).HasMaxLength(255);
            e.Property(c => c.Agent).HasMaxLength(100);
            e.Property(c => c.Model).HasMaxLength(255);
            e.Property(c => c.AgentPercentage).HasPrecision(5, 1);

            e.HasIndex(c => new { c.TeamId, c.RepoName, c.CommitSha })
              .IsUnique()
              .HasDatabaseName("UQ_Commit");
            e.HasIndex(c => c.TeamId).HasDatabaseName("IX_Commits_Team");
            e.HasIndex(c => new { c.TeamId, c.RepoName }).HasDatabaseName("IX_Commits_Repo");
            e.HasIndex(c => new { c.TeamId, c.CommitAuthor }).HasDatabaseName("IX_Commits_Author");
            e.HasIndex(c => new { c.TeamId, c.CommittedAt }).HasDatabaseName("IX_Commits_Date");
            e.HasIndex(c => new { c.TeamId, c.Agent }).HasDatabaseName("IX_Commits_Agent");
        });
    }
}
```

- [ ] **Step 4: Write `Auth/ApiKeyHasher.cs`**

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Forge.Metrics.Api.Auth;

public static class ApiKeyHasher
{
    public static string Hash(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexStringLower(bytes);
    }
}
```

- [ ] **Step 5: Commit**

```bash
git add api/Forge.Metrics.Api/Data/ api/Forge.Metrics.Api/Auth/ApiKeyHasher.cs
git commit -m "feat(api): EF Core entities, DbContext, api key hasher"
```

---

## Task 7: API key endpoint filter

**Files:**
- Create: `api/Forge.Metrics.Api/Auth/ApiKeyFilter.cs`

- [ ] **Step 1: Write `Auth/ApiKeyFilter.cs`**

```csharp
using Forge.Metrics.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Forge.Metrics.Api.Auth;

public sealed class ApiKeyFilter : IEndpointFilter
{
    public const string TeamItemKey = "Forge.Team";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (!http.Request.Headers.TryGetValue("X-API-Key", out var values) || values.Count == 0)
            return Results.Unauthorized();

        var apiKey = values.ToString();
        if (string.IsNullOrWhiteSpace(apiKey))
            return Results.Unauthorized();

        var db = http.RequestServices.GetRequiredService<ForgeDbContext>();
        var hash = ApiKeyHasher.Hash(apiKey);
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.ApiKeyHash == hash);
        if (team is null)
            return Results.Unauthorized();

        http.Items[TeamItemKey] = team;
        return await next(ctx);
    }
}

public static class HttpContextTeamExtensions
{
    public static Team RequireTeam(this HttpContext http) =>
        (Team)(http.Items[ApiKeyFilter.TeamItemKey]
               ?? throw new InvalidOperationException("Team missing from HttpContext.Items — did the ApiKeyFilter run?"));
}
```

- [ ] **Step 2: Commit**

```bash
git add api/Forge.Metrics.Api/Auth/ApiKeyFilter.cs
git commit -m "feat(api): X-API-Key endpoint filter"
```

---

## Task 8: Note parser — TDD

**Files:**
- Create: `api/Forge.Metrics.Api/Parsing/ParsedNote.cs`
- Create: `api/Forge.Metrics.Tests/NoteParserTests.cs`
- Create: `api/Forge.Metrics.Api/Parsing/NoteParser.cs`

Splits `note_content` into a file map and a JSON section (separator `---`).

- [ ] **Step 1: Write `Parsing/ParsedNote.cs`**

```csharp
using System.Text.Json.Nodes;

namespace Forge.Metrics.Api.Parsing;

public sealed record FileMapEntry(string File, string AttributionId, int LineCount)
{
    public bool IsHuman => AttributionId.StartsWith("h_", StringComparison.Ordinal);
}

public sealed class ParsedNote
{
    public List<FileMapEntry> Entries { get; } = new();
    public Dictionary<string, JsonObject> Prompts { get; } = new();
    public Dictionary<string, JsonObject> Humans { get; } = new();
}
```

- [ ] **Step 2: Write the failing parser tests**

`api/Forge.Metrics.Tests/NoteParserTests.cs`:

```csharp
using Forge.Metrics.Api.Parsing;
using Xunit;

namespace Forge.Metrics.Tests;

public class NoteParserTests
{
    private const string MixedNote = """
calc-test-ai.json
  h_dca485b1adf836 11-13
  33d7da781a966cb5 9-10
human-only.json
  h_dca485b1adf836 1-3
---
{
  "prompts": {
    "33d7da781a966cb5": {
      "agent": "claude",
      "model": "claude-opus-4-7",
      "accepted": true,
      "overridden_lines": 1
    }
  },
  "humans": {
    "h_dca485b1adf836": { "author": "Dmytro Shamsiiev" }
  }
}
""";

    private const string PureHumanNote = """
---
{"prompts": {}, "humans": {"h_aaa": {"author": "Bob"}}}
""";

    private const string MultiRangeNote = """
foo.py
  33d7da781a966cb5 1-2,5,7-9
---
{"prompts": {"33d7da781a966cb5": {"agent": "claude", "model": "x", "accepted": true, "overridden_lines": 0}}, "humans": {}}
""";

    [Fact]
    public void Parses_mixed_note_filemap_entries()
    {
        var parsed = NoteParser.Parse(MixedNote);
        Assert.Equal(3, parsed.Entries.Count);
        Assert.Equal(new FileMapEntry("calc-test-ai.json", "h_dca485b1adf836", 3), parsed.Entries[0]);
        Assert.Equal(new FileMapEntry("calc-test-ai.json", "33d7da781a966cb5", 2), parsed.Entries[1]);
        Assert.Equal(new FileMapEntry("human-only.json", "h_dca485b1adf836", 3), parsed.Entries[2]);
    }

    [Fact]
    public void Parses_mixed_note_json_metadata()
    {
        var parsed = NoteParser.Parse(MixedNote);
        Assert.Contains("33d7da781a966cb5", parsed.Prompts.Keys);
        Assert.Equal("claude", parsed.Prompts["33d7da781a966cb5"]["agent"]!.GetValue<string>());
        Assert.Equal(1, parsed.Prompts["33d7da781a966cb5"]["overridden_lines"]!.GetValue<int>());
        Assert.Equal("Dmytro Shamsiiev", parsed.Humans["h_dca485b1adf836"]["author"]!.GetValue<string>());
    }

    [Fact]
    public void Parses_pure_human_note_with_empty_filemap()
    {
        var parsed = NoteParser.Parse(PureHumanNote);
        Assert.Empty(parsed.Entries);
        Assert.Empty(parsed.Prompts);
        Assert.Equal("Bob", parsed.Humans["h_aaa"]["author"]!.GetValue<string>());
    }

    [Fact]
    public void Parses_multiple_ranges_summing_lines()
    {
        var parsed = NoteParser.Parse(MultiRangeNote);
        // 1-2 (2) + 5 (1) + 7-9 (3) = 6
        Assert.Equal(6, parsed.Entries[0].LineCount);
    }

    [Fact]
    public void Throws_when_separator_missing()
    {
        Assert.Throws<FormatException>(() => NoteParser.Parse("calc.json\n  h_a 1-2\n"));
    }
}
```

- [ ] **Step 3: Run tests and confirm they fail**

```bash
docker compose run --rm api dotnet test /src/Forge.Metrics.sln --filter FullyQualifiedName~NoteParserTests
```

Expected: build error or test failure — `NoteParser.Parse` doesn't exist yet.

- [ ] **Step 4: Implement `Parsing/NoteParser.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Forge.Metrics.Api.Parsing;

public static partial class NoteParser
{
    [GeneratedRegex(@"^(\d+)(?:-(\d+))?$")]
    private static partial Regex RangeToken();

    public static ParsedNote Parse(string noteContent)
    {
        if (!noteContent.Contains("---", StringComparison.Ordinal))
            throw new FormatException("note missing '---' separator between file map and JSON");

        // Split on a line containing only '---'. Tolerate leading separator (pure human commits).
        var idx = noteContent.IndexOf("---", StringComparison.Ordinal);
        var head = noteContent[..idx];
        var tail = noteContent[(idx + 3)..].TrimStart('\r', '\n').TrimEnd();

        var parsed = new ParsedNote();
        string? currentFile = null;
        foreach (var rawLine in head.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                if (currentFile is null)
                    throw new FormatException($"indented line before any filename: {line}");
                var trimmed = line.Trim();
                var spaceIdx = trimmed.IndexOf(' ');
                if (spaceIdx <= 0)
                    throw new FormatException($"malformed file map line: {line}");
                var attrId = trimmed[..spaceIdx];
                var ranges = trimmed[(spaceIdx + 1)..].Trim();
                parsed.Entries.Add(new FileMapEntry(currentFile, attrId, CountLines(ranges)));
            }
            else
            {
                currentFile = line.Trim();
            }
        }

        if (tail.Length > 0)
        {
            using var doc = JsonDocument.Parse(tail);
            var root = JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject();
            if (root["prompts"] is JsonObject prompts)
                foreach (var kv in prompts)
                    if (kv.Value is JsonObject obj) parsed.Prompts[kv.Key] = obj;
            if (root["humans"] is JsonObject humans)
                foreach (var kv in humans)
                    if (kv.Value is JsonObject obj) parsed.Humans[kv.Key] = obj;
        }

        return parsed;
    }

    private static int CountLines(string ranges)
    {
        var total = 0;
        foreach (var rawTok in ranges.Split(','))
        {
            var tok = rawTok.Trim();
            if (tok.Length == 0) continue;
            var m = RangeToken().Match(tok);
            if (!m.Success)
                throw new FormatException($"bad range token: {tok}");
            var start = int.Parse(m.Groups[1].Value);
            var end = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : start;
            if (end < start)
                throw new FormatException($"inverted range: {tok}");
            total += end - start + 1;
        }
        return total;
    }
}
```

- [ ] **Step 5: Run tests and confirm green**

```bash
docker compose run --rm api dotnet test /src/Forge.Metrics.sln --filter FullyQualifiedName~NoteParserTests
```

Expected: 5 tests pass.

- [ ] **Step 6: Commit**

```bash
git add api/Forge.Metrics.Api/Parsing/ParsedNote.cs api/Forge.Metrics.Api/Parsing/NoteParser.cs api/Forge.Metrics.Tests/NoteParserTests.cs
git commit -m "feat(api): note parser with file map + JSON metadata"
```

---

## Task 9: Attribution calculator — TDD

**Files:**
- Create: `api/Forge.Metrics.Tests/AttributionCalculatorTests.cs`
- Create: `api/Forge.Metrics.Api/Parsing/AttributionCalculator.cs`

Implements spec §2 step 4 + §1 examples.

- [ ] **Step 1: Write the failing tests**

`api/Forge.Metrics.Tests/AttributionCalculatorTests.cs`:

```csharp
using Forge.Metrics.Api.Parsing;
using Xunit;

namespace Forge.Metrics.Tests;

public class AttributionCalculatorTests
{
    private const string Mixed = """
calc-test-ai.json
  h_dca485b1adf836 11-13
  33d7da781a966cb5 9-10
human-only.json
  h_dca485b1adf836 1-3
---
{"prompts": {"33d7da781a966cb5": {"agent":"claude","model":"claude-opus-4-7","accepted":true,"overridden_lines":1}},
 "humans": {"h_dca485b1adf836":{"author":"Dmytro Shamsiiev"}}}
""";

    private const string Sibling = """
calc-test-sibling.json
  h_dca485b1adf836 7-8
  33d7da781a966cb5 5-6
---
{"prompts": {"33d7da781a966cb5": {"agent":"claude","model":"x","accepted":true,"overridden_lines":1}},
 "humans": {"h_dca485b1adf836":{"author":"X"}}}
""";

    private const string PureHuman = """
---
{"prompts": {}, "humans": {"h_a": {"author": "Bob"}}}
""";

    private const string MultiAgent = """
foo.py
  prompt_claude 1-3
  prompt_copilot 4-5
---
{"prompts": {
  "prompt_claude": {"agent":"claude","model":"opus","accepted":true,"overridden_lines":0},
  "prompt_copilot": {"agent":"github-copilot","model":"gpt-5","accepted":true,"overridden_lines":0}
 }, "humans": {}}
""";

    [Fact]
    public void Mixed_commit_is_25_percent()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(Mixed));
        Assert.Equal(2, r.AgentLines);
        Assert.Equal(6, r.HumanLines);
        Assert.Equal(25.0m, r.AgentPercentage);
        Assert.Equal(1, r.OverriddenLines);
    }

    [Fact]
    public void Sibling_commit_is_50_percent()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(Sibling));
        Assert.Equal(2, r.AgentLines);
        Assert.Equal(2, r.HumanLines);
        Assert.Equal(50.0m, r.AgentPercentage);
    }

    [Fact]
    public void Pure_human_with_no_filemap_returns_zeros()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(PureHuman));
        Assert.Equal(0, r.AgentLines);
        Assert.Equal(0, r.HumanLines);
        Assert.Equal(0m, r.AgentPercentage);
    }

    [Fact]
    public void Pure_human_with_diff_additions_fills_human_lines()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(PureHuman), enrichedDiffAdditions: 12);
        Assert.Equal(0, r.AgentLines);
        Assert.Equal(12, r.HumanLines);
        Assert.Equal(0m, r.AgentPercentage);
    }

    [Fact]
    public void Multi_agent_records_comma_separated_agents()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(MultiAgent));
        Assert.Equal(5, r.AgentLines);
        Assert.Equal(0, r.HumanLines);
        Assert.Equal(100m, r.AgentPercentage);
        Assert.Equal("claude,github-copilot", r.Agents);
        Assert.Equal("opus,gpt-5", r.Models);
    }

    [Fact]
    public void Single_agent_returns_single_agent_string()
    {
        var r = AttributionCalculator.Compute(NoteParser.Parse(Mixed));
        Assert.Equal("claude", r.Agents);
        Assert.Equal("claude-opus-4-7", r.Models);
    }
}
```

- [ ] **Step 2: Run tests, confirm failure**

```bash
docker compose run --rm api dotnet test /src/Forge.Metrics.sln --filter FullyQualifiedName~AttributionCalculatorTests
```

Expected: build error — type doesn't exist.

- [ ] **Step 3: Implement `Parsing/AttributionCalculator.cs`**

```csharp
namespace Forge.Metrics.Api.Parsing;

public sealed record AttributionResult(
    int AgentLines,
    int HumanLines,
    decimal AgentPercentage,
    int OverriddenLines,
    string? Agents,
    string? Models);

public static class AttributionCalculator
{
    public static AttributionResult Compute(ParsedNote parsed, int? enrichedDiffAdditions = null)
    {
        var agentLines = parsed.Entries.Where(e => !e.IsHuman).Sum(e => e.LineCount);
        var humanLines = parsed.Entries.Where(e => e.IsHuman).Sum(e => e.LineCount);

        if (agentLines == 0 && humanLines == 0 && enrichedDiffAdditions is > 0)
            humanLines = enrichedDiffAdditions.Value;

        var total = agentLines + humanLines;
        var pct = total > 0
            ? Math.Round((decimal)agentLines / total * 100m, 1)
            : 0m;

        var overridden = 0;
        foreach (var prompt in parsed.Prompts.Values)
            if (prompt["overridden_lines"] is { } v && v.GetValue<int>() is var n)
                overridden += n;

        var contributingPromptIds = parsed.Entries
            .Where(e => !e.IsHuman)
            .Select(e => e.AttributionId)
            .Distinct()
            .ToArray();

        string? JoinDistinct(string field) =>
            contributingPromptIds
                .Select(pid => parsed.Prompts.TryGetValue(pid, out var p) ? p[field]?.GetValue<string>() : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .DefaultIfEmpty(null)
                .Aggregate((a, b) => a is null ? b : (b is null ? a : $"{a},{b}"));

        return new AttributionResult(
            agentLines,
            humanLines,
            pct,
            overridden,
            JoinDistinct("agent"),
            JoinDistinct("model"));
    }
}
```

- [ ] **Step 4: Run tests, confirm green**

```bash
docker compose run --rm api dotnet test /src/Forge.Metrics.sln --filter FullyQualifiedName~AttributionCalculatorTests
```

Expected: 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add api/Forge.Metrics.Api/Parsing/AttributionCalculator.cs api/Forge.Metrics.Tests/AttributionCalculatorTests.cs
git commit -m "feat(api): attribution calculator from file map"
```

---

## Task 10: DTOs

**Files:**
- Create: `api/Forge.Metrics.Api/Dtos/IngestPayload.cs`
- Create: `api/Forge.Metrics.Api/Dtos/IngestResponse.cs`
- Create: `api/Forge.Metrics.Api/Dtos/MetricsDtos.cs`

The bash setup script POSTs snake_case JSON (matching git-ai's payload). We configure `JsonNamingPolicy.SnakeCaseLower` globally in Program.cs (Task 12) so DTOs can use natural C# names.

- [ ] **Step 1: Write `Dtos/IngestPayload.cs`**

```csharp
namespace Forge.Metrics.Api.Dtos;

public sealed record IngestPayload(
    string RepoName,
    string CommitSha,
    string NoteContent,
    string? RepoUrl = null,
    string? Branch = null,
    bool IsDefaultBranch = false,
    string? CommitAuthor = null,
    string? Agent = null,
    string? Model = null,
    int? DiffAdditions = null,
    int? DiffDeletions = null,
    DateTime? CommittedAt = null);
```

- [ ] **Step 2: Write `Dtos/IngestResponse.cs`**

```csharp
namespace Forge.Metrics.Api.Dtos;

public sealed record IngestResponse(
    Guid CommitId,
    int AgentLines,
    int HumanLines,
    decimal AgentPercentage,
    int OverriddenLines,
    bool Duplicate);
```

- [ ] **Step 3: Write `Dtos/MetricsDtos.cs`**

```csharp
namespace Forge.Metrics.Api.Dtos;

public sealed record SummaryResponse(
    int PeriodDays,
    int TotalCommits,
    int AiCommits,
    decimal AiPercentage,
    int TotalAiLines,
    int TotalHumanLines);

public sealed record ByAgentRow(string? Agent, int Commits, int AiLines, decimal AvgPercentage);
public sealed record ByDeveloperRow(string? Author, int Commits, int AiLines, decimal AiPercentage);
public sealed record ByRepoRow(string RepoName, int Commits, int AiLines, decimal AiPercentage);
```

- [ ] **Step 4: Commit**

```bash
git add api/Forge.Metrics.Api/Dtos/
git commit -m "feat(api): request/response DTOs"
```

---

## Task 11: Endpoint extension methods

**Files:**
- Create: `api/Forge.Metrics.Api/Endpoints/IngestEndpoints.cs`
- Create: `api/Forge.Metrics.Api/Endpoints/MetricsEndpoints.cs`
- Create: `api/Forge.Metrics.Api/Endpoints/SetupEndpoints.cs`

- [ ] **Step 1: Write `Endpoints/IngestEndpoints.cs`**

```csharp
using Forge.Metrics.Api.Auth;
using Forge.Metrics.Api.Data;
using Forge.Metrics.Api.Dtos;
using Forge.Metrics.Api.Parsing;
using Microsoft.EntityFrameworkCore;

namespace Forge.Metrics.Api.Endpoints;

public static class IngestEndpoints
{
    public static IEndpointRouteBuilder MapIngest(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ingest", async (IngestPayload payload, HttpContext http, ForgeDbContext db) =>
            {
                var team = http.RequireTeam();

                ParsedNote parsed;
                try { parsed = NoteParser.Parse(payload.NoteContent); }
                catch (FormatException ex) { return Results.BadRequest(new { error = ex.Message }); }

                var attribution = AttributionCalculator.Compute(parsed, payload.DiffAdditions);

                var existing = await db.Commits.FirstOrDefaultAsync(c =>
                    c.TeamId == team.Id &&
                    c.RepoName == payload.RepoName &&
                    c.CommitSha == payload.CommitSha);

                var duplicate = existing is not null;
                var row = existing ?? new Commit
                {
                    TeamId = team.Id,
                    RepoName = payload.RepoName,
                    CommitSha = payload.CommitSha,
                };

                row.RepoUrl = payload.RepoUrl;
                row.Branch = payload.Branch;
                row.IsDefaultBranch = payload.IsDefaultBranch;
                row.CommitAuthor = payload.CommitAuthor;
                row.Agent = attribution.Agents ?? payload.Agent;
                row.Model = attribution.Models ?? payload.Model;
                row.AgentLines = attribution.AgentLines;
                row.HumanLines = attribution.HumanLines;
                row.OverriddenLines = attribution.OverriddenLines;
                row.AgentPercentage = attribution.AgentPercentage;
                row.DiffAdditions = payload.DiffAdditions ?? 0;
                row.DiffDeletions = payload.DiffDeletions ?? 0;
                row.CommittedAt = payload.CommittedAt;
                row.RawNote = payload.NoteContent;

                if (!duplicate) db.Commits.Add(row);
                await db.SaveChangesAsync();

                return Results.Ok(new IngestResponse(
                    row.Id, row.AgentLines, row.HumanLines, row.AgentPercentage, row.OverriddenLines, duplicate));
            })
            .WithTags("ingest")
            .WithSummary("Ingest a git-ai note from a developer machine")
            .AddEndpointFilter<ApiKeyFilter>();

        return app;
    }
}
```

- [ ] **Step 2: Write `Endpoints/MetricsEndpoints.cs`**

```csharp
using Forge.Metrics.Api.Auth;
using Forge.Metrics.Api.Data;
using Forge.Metrics.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Forge.Metrics.Api.Endpoints;

public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetrics(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/metrics")
            .WithTags("metrics")
            .AddEndpointFilter<ApiKeyFilter>();

        group.MapGet("/summary", async (string? period, HttpContext http, ForgeDbContext db) =>
        {
            var (cutoff, days) = ParsePeriod(period);
            var team = http.RequireTeam();
            var q = db.Commits.AsNoTracking()
                .Where(c => c.TeamId == team.Id && (c.CommittedAt ?? c.IngestedAt) >= cutoff);

            var totalCommits = await q.CountAsync();
            var aiCommits = await q.CountAsync(c => c.AgentLines > 0);
            var aiLines = await q.SumAsync(c => (int?)c.AgentLines) ?? 0;
            var humanLines = await q.SumAsync(c => (int?)c.HumanLines) ?? 0;

            return Results.Ok(new SummaryResponse(
                days,
                totalCommits,
                aiCommits,
                totalCommits == 0 ? 0m : Math.Round((decimal)aiCommits / totalCommits * 100m, 1),
                aiLines,
                humanLines));
        });

        group.MapGet("/by-agent", async (string? period, HttpContext http, ForgeDbContext db) =>
        {
            var (cutoff, _) = ParsePeriod(period);
            var team = http.RequireTeam();

            var rows = await db.Commits.AsNoTracking()
                .Where(c => c.TeamId == team.Id && (c.CommittedAt ?? c.IngestedAt) >= cutoff)
                .GroupBy(c => c.Agent)
                .Select(g => new ByAgentRow(
                    g.Key,
                    g.Count(),
                    g.Sum(c => c.AgentLines),
                    Math.Round(g.Average(c => c.AgentPercentage), 1)))
                .ToListAsync();

            return Results.Ok(rows);
        });

        group.MapGet("/by-developer", async (string? period, HttpContext http, ForgeDbContext db) =>
        {
            var (cutoff, _) = ParsePeriod(period);
            var team = http.RequireTeam();

            var raw = await db.Commits.AsNoTracking()
                .Where(c => c.TeamId == team.Id && (c.CommittedAt ?? c.IngestedAt) >= cutoff)
                .GroupBy(c => c.CommitAuthor)
                .Select(g => new
                {
                    Author = g.Key,
                    Commits = g.Count(),
                    AiLines = g.Sum(c => c.AgentLines),
                    HumanLines = g.Sum(c => c.HumanLines),
                })
                .ToListAsync();

            var rows = raw.Select(r =>
            {
                var total = r.AiLines + r.HumanLines;
                var pct = total == 0 ? 0m : Math.Round((decimal)r.AiLines / total * 100m, 1);
                return new ByDeveloperRow(r.Author, r.Commits, r.AiLines, pct);
            });

            return Results.Ok(rows);
        });

        group.MapGet("/by-repo", async (string? period, HttpContext http, ForgeDbContext db) =>
        {
            var (cutoff, _) = ParsePeriod(period);
            var team = http.RequireTeam();

            var raw = await db.Commits.AsNoTracking()
                .Where(c => c.TeamId == team.Id && (c.CommittedAt ?? c.IngestedAt) >= cutoff)
                .GroupBy(c => c.RepoName)
                .Select(g => new
                {
                    RepoName = g.Key,
                    Commits = g.Count(),
                    AiLines = g.Sum(c => c.AgentLines),
                    HumanLines = g.Sum(c => c.HumanLines),
                })
                .ToListAsync();

            var rows = raw.Select(r =>
            {
                var total = r.AiLines + r.HumanLines;
                var pct = total == 0 ? 0m : Math.Round((decimal)r.AiLines / total * 100m, 1);
                return new ByRepoRow(r.RepoName, r.Commits, r.AiLines, pct);
            });

            return Results.Ok(rows);
        });

        return app;
    }

    private static (DateTime cutoff, int days) ParsePeriod(string? period)
    {
        var raw = string.IsNullOrEmpty(period) ? "30d" : period;
        if (!raw.EndsWith('d') || !int.TryParse(raw.AsSpan(0, raw.Length - 1), out var days) || days <= 0)
            throw new BadHttpRequestException($"invalid period '{raw}', expected e.g. '7d', '30d', '90d'");
        return (DateTime.UtcNow.AddDays(-days), days);
    }
}
```

- [ ] **Step 3: Write `Endpoints/SetupEndpoints.cs`**

```csharp
using Forge.Metrics.Api.Configuration;
using Forge.Metrics.Api.Auth;
using Forge.Metrics.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Forge.Metrics.Api.Endpoints;

public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetup(this IEndpointRouteBuilder app)
    {
        app.MapGet("/setup/{teamId:guid}/{apiKey}", async (
            Guid teamId, string apiKey,
            ForgeDbContext db,
            IOptions<ForgeOptions> opts) =>
        {
            var team = await ValidateAsync(db, teamId, apiKey);
            if (team is null) return Results.NotFound("team or api key not found");

            var tmplPath = Path.Combine(opts.Value.ScriptsPath, "setup.sh.tmpl");
            var rendered = (await File.ReadAllTextAsync(tmplPath))
                .Replace("__API_URL__", opts.Value.PublicApiUrl)
                .Replace("__API_KEY__", apiKey)
                .Replace("__TEAM_ID__", team.Id.ToString());

            return Results.Text(rendered, "text/x-shellscript");
        })
        .WithTags("setup")
        .WithSummary("Bash setup script for the developer machine");

        app.MapGet("/setup/{teamId:guid}/{apiKey}/enrich-and-post.sh", async (
            Guid teamId, string apiKey,
            ForgeDbContext db,
            IOptions<ForgeOptions> opts) =>
        {
            var team = await ValidateAsync(db, teamId, apiKey);
            if (team is null) return Results.NotFound();

            var path = Path.Combine(opts.Value.ScriptsPath, "enrich-and-post.sh");
            var body = await File.ReadAllTextAsync(path);
            return Results.Text(body, "text/x-shellscript");
        })
        .WithTags("setup")
        .WithSummary("Latest enrich-and-post.sh hook script");

        return app;
    }

    private static async Task<Team?> ValidateAsync(ForgeDbContext db, Guid teamId, string apiKey)
    {
        var hash = ApiKeyHasher.Hash(apiKey);
        var team = await db.Teams.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ApiKeyHash == hash);
        return team is null || team.Id != teamId ? null : team;
    }
}
```

- [ ] **Step 4: Commit**

```bash
git add api/Forge.Metrics.Api/Endpoints/
git commit -m "feat(api): ingest, metrics, setup endpoints"
```

---

## Task 12: Program.cs — wire it all together

**Files:**
- Create: `api/Forge.Metrics.Api/Program.cs`

- [ ] **Step 1: Write `Program.cs`**

```csharp
using System.Text.Json;
using Forge.Metrics.Api.Auth;
using Forge.Metrics.Api.Configuration;
using Forge.Metrics.Api.Data;
using Forge.Metrics.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForgeOptions>(builder.Configuration.GetSection(ForgeOptions.SectionName));

builder.Services.AddDbContext<ForgeDbContext>(opts =>
    opts.UseSqlServer(
        builder.Configuration.GetConnectionString("Default"),
        sql => sql.EnableRetryOnFailure()));

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Forge AI Metrics", Version = "v1" });
    c.AddSecurityDefinition("ApiKey", new()
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "X-API-Key",
        Description = "Team API key (from /api/ingest auth, set in your stack's .env)"
    });
    c.AddSecurityRequirement(new()
    {
        [new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "ApiKey" } }] = []
    });
});

builder.Services.AddScoped<ApiKeyFilter>();

var app = builder.Build();

await BootstrapDatabaseAsync(app.Services);

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Forge AI Metrics v1"));

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("health");

app.MapIngest();
app.MapMetrics();
app.MapSetup();

app.Run();

static async Task BootstrapDatabaseAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
    var opts = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ForgeOptions>>().Value;
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Bootstrap");

    Exception? last = null;
    for (var attempt = 1; attempt <= 30; attempt++)
    {
        try
        {
            await db.Database.EnsureCreatedAsync();
            last = null;
            break;
        }
        catch (Exception ex)
        {
            last = ex;
            logger.LogWarning("waiting for sql server (attempt {Attempt}): {Message}", attempt, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
    if (last is not null) throw new InvalidOperationException("sql server unreachable after 30 attempts", last);

    var hash = ApiKeyHasher.Hash(opts.SeedTeamApiKey);
    if (!await db.Teams.AnyAsync(t => t.ApiKeyHash == hash))
    {
        db.Teams.Add(new Team { Name = opts.SeedTeamName, ApiKeyHash = hash });
        await db.SaveChangesAsync();
        logger.LogInformation("seeded team {Name}", opts.SeedTeamName);
    }
}

public partial class Program;  // for WebApplicationFactory if added later
```

- [ ] **Step 2: Build the image and start the stack**

```bash
cd /Users/dmytroshamsiiev/Projects/metrics-architecture
cp .env.example .env
docker compose up -d --build
docker compose logs -f api
```

Expected (within ~60s): `Now listening on: http://[::]:8080` and `seeded team Platform Team`.

- [ ] **Step 3: Commit**

```bash
git add api/Forge.Metrics.Api/Program.cs
git commit -m "feat(api): Program.cs wires DI, EF Core, Swagger, endpoints, bootstrap"
```

---

## Task 13: Setup script + enrich-and-post.sh (host-side artifacts)

**Files:**
- Create: `scripts/enrich-and-post.sh`
- Create: `scripts/setup.sh.tmpl`

- [ ] **Step 1: Write `scripts/enrich-and-post.sh`**

```bash
#!/usr/bin/env bash
# enrich-and-post.sh — git-ai post_notes_updated hook.
# Reads JSON payload from stdin, augments with diff stats and committed_at,
# POSTs to the Forge AI ingest endpoint.
set -euo pipefail

CONFIG="${HOME}/.forge-ai/config.json"
if [[ ! -f "$CONFIG" ]]; then
  echo "[forge-ai] missing $CONFIG — re-run the setup command" >&2
  exit 0
fi

API_URL="$(jq -r '.api_url' "$CONFIG")"
API_KEY="$(jq -r '.api_key' "$CONFIG")"
PROJECT_ROOT="$(jq -r '.project_root' "$CONFIG")"

PAYLOAD="$(cat)"
REPO_URL="$(printf '%s' "$PAYLOAD" | jq -r '.repo_url // empty')"
COMMIT_SHA="$(printf '%s' "$PAYLOAD" | jq -r '.commit_sha // empty')"

REPO_DIR=""
if [[ -n "$REPO_URL" && -d "$PROJECT_ROOT" ]]; then
  while IFS= read -r -d '' git_dir; do
    candidate="$(dirname "$git_dir")"
    remote="$(git -C "$candidate" config --get remote.origin.url 2>/dev/null || true)"
    if [[ "$remote" == "$REPO_URL" ]]; then
      REPO_DIR="$candidate"
      break
    fi
  done < <(find "$PROJECT_ROOT" -maxdepth 6 -type d -name '.git' -print0 2>/dev/null)
fi

DIFF_ADD=""
DIFF_DEL=""
COMMITTED_AT=""
if [[ -n "$REPO_DIR" && -n "$COMMIT_SHA" ]]; then
  numstat="$(git -C "$REPO_DIR" diff --numstat "${COMMIT_SHA}^!" 2>/dev/null || true)"
  if [[ -n "$numstat" ]]; then
    DIFF_ADD="$(awk '{a+=$1} END {print a+0}' <<<"$numstat")"
    DIFF_DEL="$(awk '{d+=$2} END {print d+0}' <<<"$numstat")"
  fi
  COMMITTED_AT="$(git -C "$REPO_DIR" log -1 --format=%aI "$COMMIT_SHA" 2>/dev/null || true)"
fi

ENRICHED="$(jq \
  --arg add "${DIFF_ADD}" \
  --arg del "${DIFF_DEL}" \
  --arg ts  "${COMMITTED_AT}" \
  '
    . as $p
    | $p
    + ( if $add != "" then {diff_additions: ($add|tonumber)} else {} end )
    + ( if $del != "" then {diff_deletions: ($del|tonumber)} else {} end )
    + ( if $ts  != "" then {committed_at: $ts} else {} end )
  ' <<<"$PAYLOAD")"

for attempt in 1 2 3; do
  if curl -fsS -X POST "$API_URL/api/ingest" \
       -H "Content-Type: application/json" \
       -H "X-API-Key: $API_KEY" \
       --data "$ENRICHED" \
       -o /tmp/forge-ai-resp.json; then
    exit 0
  fi
  sleep 2
done

echo "[forge-ai] failed after 3 attempts (commit ${COMMIT_SHA})" >&2
exit 0  # never block git-ai on telemetry failure
```

- [ ] **Step 2: Make it executable**

```bash
chmod +x /Users/dmytroshamsiiev/Projects/metrics-architecture/scripts/enrich-and-post.sh
```

- [ ] **Step 3: Write `scripts/setup.sh.tmpl`**

```bash
#!/usr/bin/env bash
# Forge AI Metrics — developer setup. Idempotent: safe to re-run.
set -euo pipefail

API_URL="__API_URL__"
API_KEY="__API_KEY__"
TEAM_ID="__TEAM_ID__"

FORGE_DIR="${HOME}/.forge-ai"
mkdir -p "$FORGE_DIR"

need() { command -v "$1" >/dev/null 2>&1 || { echo "[forge-ai] missing dependency: $1" >&2; exit 1; }; }
need curl
need jq
need git

if ! command -v git-ai >/dev/null 2>&1; then
  echo "[forge-ai] git-ai is not installed. See https://github.com/git-ai-project/git-ai" >&2
  exit 1
fi

detect_project_root() {
  for cand in "$HOME/Projects" "$HOME/Code" "$HOME/work" "$HOME/src" "$HOME/dev"; do
    if [[ -d "$cand" ]] && find "$cand" -maxdepth 4 -type d -name '.git' -print -quit 2>/dev/null | grep -q .; then
      printf '%s' "$cand"
      return
    fi
  done
  printf '%s' "$HOME"
}
PROJECT_ROOT="${FORGE_PROJECT_ROOT:-$(detect_project_root)}"
echo "[forge-ai] project_root: $PROJECT_ROOT  (override with FORGE_PROJECT_ROOT=...)"

curl -fsS "${API_URL}/setup/${TEAM_ID}/${API_KEY}/enrich-and-post.sh" \
  -o "${FORGE_DIR}/enrich-and-post.sh"
chmod +x "${FORGE_DIR}/enrich-and-post.sh"

cat > "${FORGE_DIR}/config.json" <<JSON
{
  "api_url": "${API_URL}",
  "api_key": "${API_KEY}",
  "team_id": "${TEAM_ID}",
  "project_root": "${PROJECT_ROOT}"
}
JSON

git config --global --replace-all git_ai_hooks.post_notes_updated "${FORGE_DIR}/enrich-and-post.sh"
git config --global git_ai.prompt_storage local || true

echo "[forge-ai] hook: $(git config --global --get git_ai_hooks.post_notes_updated)"
echo "[forge-ai] config: ${FORGE_DIR}/config.json"
echo "[forge-ai] setup complete."
```

- [ ] **Step 4: Commit**

```bash
git add scripts/
git commit -m "feat: developer setup script + enrich-and-post hook"
```

---

## Task 14: Manual smoke verification

This task verifies wiring before onboarding. No new code.

- [ ] **Step 1: Confirm services healthy**

```bash
docker compose ps
```

Expected: `mssql` and `api` both `running`/`healthy`.

- [ ] **Step 2: Open Swagger**

```bash
open http://localhost:8000/swagger
```

Expected: Swagger UI lists `/api/ingest`, `/api/metrics/{summary,by-agent,by-developer,by-repo}`, `/setup/{teamId}/{apiKey}`, `/health`.

- [ ] **Step 3: Read the seeded team id**

```bash
docker compose exec -T mssql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa \
  -P "$(grep MSSQL_SA_PASSWORD .env | cut -d= -f2)" \
  -d forge_ai_metrics \
  -Q "SELECT Id, Name FROM Teams"
```

Record the team UUID.

- [ ] **Step 4: Hit the setup endpoint**

```bash
curl -s "http://localhost:8000/setup/<TEAM_UUID>/$(grep SEED_TEAM_API_KEY .env | cut -d= -f2)" | head
```

Expected: bash script starting with `#!/usr/bin/env bash`.

- [ ] **Step 5: Manual ingest sanity check**

```bash
curl -s -X POST http://localhost:8000/api/ingest \
  -H "Content-Type: application/json" \
  -H "X-API-Key: $(grep SEED_TEAM_API_KEY .env | cut -d= -f2)" \
  -d '{
    "repo_name":"manual-smoke",
    "commit_sha":"abc1234",
    "note_content":"foo.cs\n  prompt_a 1-3\n---\n{\"prompts\":{\"prompt_a\":{\"agent\":\"claude\",\"model\":\"opus\",\"accepted\":true,\"overridden_lines\":0}},\"humans\":{}}"
  }' | jq
```

Expected: `agent_lines: 3, human_lines: 0, agent_percentage: 100.0, duplicate: false`.

- [ ] **Step 6: Re-post to confirm idempotency**

```bash
# (same command — `duplicate` should flip to true)
```

- [ ] **Step 7: Manual metrics check**

```bash
curl -s -H "X-API-Key: $(grep SEED_TEAM_API_KEY .env | cut -d= -f2)" \
  "http://localhost:8000/api/metrics/summary?period=30d" | jq
```

Expected: `total_commits >= 1`.

If anything fails, fix and re-run from Step 1. No commit.

---

## Task 15: Onboard the developer machine

Runs on the host, not in Docker.

**Prerequisites:**
- `git-ai` installed and daemon running (`git-ai --version`).
- `jq` (`brew install jq`).
- A repo under `~/Projects` (or `$FORGE_PROJECT_ROOT`).

- [ ] **Step 1: Read TEAM_ID and API_KEY from your stack**

```bash
TEAM_ID="$(docker compose exec -T mssql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa \
  -P "$(grep MSSQL_SA_PASSWORD .env | cut -d= -f2)" -d forge_ai_metrics \
  -Q "SET NOCOUNT ON; SELECT TOP 1 CAST(Id AS NVARCHAR(40)) FROM Teams" -h -1 | tr -d '[:space:]')"
API_KEY="$(grep SEED_TEAM_API_KEY .env | cut -d= -f2)"
echo "TEAM_ID=$TEAM_ID  API_KEY=$API_KEY"
```

- [ ] **Step 2: Run the onboarding command**

```bash
curl -s "http://localhost:8000/setup/${TEAM_ID}/${API_KEY}" | bash
```

Expected end of output: `[forge-ai] setup complete.`

- [ ] **Step 3: Verify the hook is registered**

```bash
git config --global --get git_ai_hooks.post_notes_updated
cat ~/.forge-ai/config.json
ls -l ~/.forge-ai/enrich-and-post.sh
```

Expected: hook path is `~/.forge-ai/enrich-and-post.sh`; config has `api_url`, `api_key`, `team_id`, `project_root`.

- [ ] **Step 4: Make a real commit in this repo**

```bash
cd /Users/dmytroshamsiiev/Projects/metrics-architecture
echo "# trigger" >> README.md
git add README.md
git commit -m "test: trigger git-ai post_notes_updated"
```

- [ ] **Step 5: Wait for the daemon, then check the hook ran**

```bash
sleep 5
ls -lt /tmp/forge-ai-resp.json
cat /tmp/forge-ai-resp.json | jq
```

Expected: response with `commit_id` UUID and attribution numbers.

- [ ] **Step 6: Confirm the row landed in the DB**

```bash
docker compose exec -T mssql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa \
  -P "$(grep MSSQL_SA_PASSWORD .env | cut -d= -f2)" -d forge_ai_metrics \
  -Q "SELECT TOP 5 RepoName, CommitSha, Agent, AgentLines, HumanLines, AgentPercentage FROM Commits ORDER BY IngestedAt DESC"
```

Expected: a row for the commit, `RepoName = 'metrics-architecture'`.

- [ ] **Step 7: Confirm via the metrics API**

```bash
curl -s -H "X-API-Key: ${API_KEY}" "http://localhost:8000/api/metrics/summary?period=30d" | jq
curl -s -H "X-API-Key: ${API_KEY}" "http://localhost:8000/api/metrics/by-repo?period=30d" | jq
```

Expected: `metrics-architecture` appears in `by-repo`.

**Troubleshooting playbook** if Step 5 yields nothing:
- `cat ~/.forge-ai/config.json` — verify config exists.
- `git config --global --get-all git_ai_hooks.post_notes_updated` — single hook entry only.
- Manually invoke the hook:
  ```bash
  echo '{"repo_url":"<repo url>","commit_sha":"<sha>","repo_name":"metrics-architecture","note_content":"---\n{\"prompts\":{},\"humans\":{}}"}' \
    | ~/.forge-ai/enrich-and-post.sh && cat /tmp/forge-ai-resp.json
  ```
- API logs: `docker compose logs -f api`.
- If `git-ai` isn't producing notes for this repo, that's upstream (spec §9.3, §9.4).

No commit — verification only.

---

## Task 16: README — final docs

**Files:**
- Modify: `README.md` (full rewrite)

- [ ] **Step 1: Replace `README.md`**

```markdown
# Forge AI Metrics — POC (.NET 10 + EF Core)

End-to-end POC of the architecture in [`2026-04-28-forge-ai-metrics-architecture.md`](./2026-04-28-forge-ai-metrics-architecture.md).

## What's inside

- **MS SQL Server 2022** (Docker — `mcr.microsoft.com/mssql/server:2022-latest`)
- **ASP.NET Core 10** minimal-APIs backend with **EF Core 10**
- **Swagger UI** at `/swagger` via `Swashbuckle.AspNetCore`
- **`GET /setup/{teamId}/{apiKey}`** that emits a bash installer
- **`enrich-and-post.sh`** hook: enriches git-ai payloads with diff stats and `committed_at`
- Idempotent DB bootstrap via EF Core `EnsureCreatedAsync()` + a seeded test team

## Quick start

```bash
cp .env.example .env
docker compose up -d --build
open http://localhost:8000/swagger
```

## Onboarding a developer machine

```bash
TEAM_ID="$(docker compose exec -T mssql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa \
  -P "$(grep MSSQL_SA_PASSWORD .env | cut -d= -f2)" -d forge_ai_metrics \
  -Q "SET NOCOUNT ON; SELECT TOP 1 CAST(Id AS NVARCHAR(40)) FROM Teams" -h -1 | tr -d '[:space:]')"
API_KEY="$(grep SEED_TEAM_API_KEY .env | cut -d= -f2)"

curl -s "http://localhost:8000/setup/${TEAM_ID}/${API_KEY}" | bash
```

The script is idempotent — re-running upgrades the hook to the latest version (spec §1).

### Prerequisites on the developer machine
- `git-ai` installed with the daemon running
- `jq`, `curl`, `git`
- Repos located under `~/Projects` (or set `FORGE_PROJECT_ROOT`)

## Endpoints

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `POST` | `/api/ingest` | `X-API-Key` | ingest a git-ai note |
| `GET` | `/api/metrics/summary?period=30d` | `X-API-Key` | totals |
| `GET` | `/api/metrics/by-agent?period=30d` | `X-API-Key` | breakdown by agent |
| `GET` | `/api/metrics/by-developer?period=30d` | `X-API-Key` | breakdown by author |
| `GET` | `/api/metrics/by-repo?period=30d` | `X-API-Key` | breakdown by repo |
| `GET` | `/setup/{teamId}/{apiKey}` | path params | bash installer |
| `GET` | `/health` | none | liveness |
| `GET` | `/swagger` | none | Swagger UI |

## Development

```bash
make up        # start stack (build images)
make logs      # tail api logs
make test      # run xUnit tests inside the api container
make db-shell  # sqlcmd into the mssql db
make down      # stop stack
```

The api container runs `dotnet watch run` against bind-mounted source — code changes restart the app without rebuilding the image.

## What this POC does NOT do (yet)

See spec §9 (known limitations) and §"Future Considerations". In particular: no row-level security, no per-developer auth, no dashboard frontend, no retry queue beyond the 3-attempt curl loop in `enrich-and-post.sh`.
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: README with quick start, onboarding, endpoints"
```

---

## Task 17: Push to GitHub

- [ ] **Step 1: Confirm the remote repo exists**

The user has already created https://github.com/ShamsiievDmytro/metrics-architecture. Verify:

```bash
gh repo view ShamsiievDmytro/metrics-architecture --json name,defaultBranchRef
```

If the repo doesn't exist:

```bash
gh repo create ShamsiievDmytro/metrics-architecture --public --description "Forge AI Metrics — POC (.NET 10 + EF Core)" --confirm
```

- [ ] **Step 2: Add remote and push**

```bash
cd /Users/dmytroshamsiiev/Projects/metrics-architecture
git remote add origin https://github.com/ShamsiievDmytro/metrics-architecture.git
git push -u origin main
```

Expected: branch `main` published, all commits visible on GitHub.

- [ ] **Step 3: Open the repo to confirm**

```bash
gh repo view ShamsiievDmytro/metrics-architecture --web
```

Verification only — no commit.

---

## Manual end-to-end acceptance test

Run on the developer machine after Task 17 to validate the architecture:

1. `cd /Users/dmytroshamsiiev/Projects/metrics-architecture && docker compose up -d`
2. Onboard with the curl-pipe-bash command (Task 15).
3. Make a commit using an AI agent (Claude Code) in any repo under `$FORGE_PROJECT_ROOT`.
4. Within ~5s: `curl -s -H "X-API-Key: $API_KEY" http://localhost:8000/api/metrics/by-repo?period=30d | jq` lists the repo.
5. Re-run the onboarding command — completes without errors (idempotency).
6. Re-submit the same commit's note manually via `/api/ingest` — `duplicate: true` in the response.

Six greens = POC validates the architecture.

---

## Self-review checklist

- [x] Spec §1 (Developer Setup) → Tasks 13, 15.
- [x] Spec §2 (Ingest API + attribution) → Tasks 7, 8, 9, 10, 11.
- [x] Spec §3 (Schema) → Tasks 3, 6.
- [x] Spec §4 (Metrics API) → Task 11.
- [x] Spec §5 (Tenant Onboarding) → Tasks 12 (seed), 13 (script), 15 (run).
- [x] Spec §6 (Idempotency) → upsert in Task 11, idempotent setup script in Task 13, bootstrap in Task 12.
- [x] Spec §7 (Security) → API key endpoint filter (Task 7), only `RawNote` stored.
- [x] Spec §8 (Validated Results) → Task 9 tests use the exact 25% / 50% / mixed cases from the spec.
- [x] Swagger requirement → `Swashbuckle.AspNetCore` at `/swagger` (Task 12, verified Task 14).
- [x] GitHub push → Task 17.
- [x] Onboarding self-test → Task 15.
- [x] No "TBD"/"TODO"/"add error handling" placeholders.
- [x] Type names consistent (`AttributionResult`, `IngestPayload`, `ParsedNote`, `FileMapEntry`, `ApiKeyFilter`).
- [x] Every code step has the actual code; every command step has expected output.
