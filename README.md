# Forge AI Metrics — POC (.NET 10 + EF Core)

End-to-end POC of the architecture in [`2026-04-28-forge-ai-metrics-architecture.md`](./2026-04-28-forge-ai-metrics-architecture.md).

## What's inside

- **MS SQL Server 2022** (Docker — `mcr.microsoft.com/mssql/server:2022-latest`)
- **ASP.NET Core 10** minimal-APIs backend with **EF Core 10**
- **Swagger UI** at `/swagger` via `Swashbuckle.AspNetCore`
- **`GET /setup/{teamId}/{apiKey}`** that emits a one-command bash installer (installs `git-ai` + `jq`, registers the `post_notes_updated` hook, writes `~/.forge-ai/config.json`)
- **`enrich-and-post.sh`** hook: enriches git-ai payloads with diff stats + `committed_at`, retries 3× then drops
- Idempotent DB bootstrap via EF Core `EnsureCreatedAsync()` with a 30-attempt connect-retry loop, plus a seeded test team

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

The script is idempotent — re-running upgrades `git-ai` (via `git-ai upgrade`) and refreshes the hook. Open a fresh shell after first install so `git-ai`'s `PATH` and IDE hooks take effect.

### What the script does
1. Installs `jq` (brew/apt/dnf/pacman/apk) and `git-ai` (`https://usegitai.com/install.sh`) if missing.
2. On re-run, runs `git-ai upgrade` (no-op when current).
3. Auto-detects `project_root` (first of `~/Projects`, `~/Code`, `~/work`, `~/src`, `~/dev` containing a `.git` dir; override with `FORGE_PROJECT_ROOT`).
4. Downloads the latest `enrich-and-post.sh` from the API into `~/.forge-ai/`.
5. Writes `~/.forge-ai/config.json` (`api_url`, `api_key`, `team_id`, `project_root`).
6. `git-ai config --add git_ai_hooks.post_notes_updated <path>` — registers the hook in git-ai's own config (NOT git's global config).
7. `git-ai config set feature_flags.git_hooks_enabled true` — required; off by default in 1.3.4.
8. `git-ai config set prompt_storage local`.

### Prerequisites on the developer machine
- macOS (Homebrew available) or Linux (apt / dnf / yum / pacman / apk)
- `curl`, `git`

## Endpoints

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `POST` | `/api/ingest` | `X-API-Key` | ingest a git-ai note |
| `GET` | `/api/metrics/summary?period=30d` | `X-API-Key` | totals |
| `GET` | `/api/metrics/by-agent?period=30d` | `X-API-Key` | breakdown by agent |
| `GET` | `/api/metrics/by-developer?period=30d` | `X-API-Key` | breakdown by author |
| `GET` | `/api/metrics/by-repo?period=30d` | `X-API-Key` | breakdown by repo |
| `GET` | `/setup/{teamId}/{apiKey}` | path params | bash installer |
| `GET` | `/setup/{teamId}/{apiKey}/enrich-and-post.sh` | path params | latest hook script |
| `GET` | `/health` | none | liveness |
| `GET` | `/swagger` | none | Swagger UI |

## Project layout

```
api/Forge.Metrics.Api/
├── Program.cs              # DI, EF Core, Swagger, snake_case JSON, bootstrap retry
├── Configuration/          # ForgeOptions
├── Data/                   # Team, Commit, ForgeDbContext (EnsureCreated-based)
├── Auth/                   # ApiKeyHasher, ApiKeyFilter (endpoint filter)
├── Parsing/                # ParsedNote, NoteParser, AttributionCalculator
├── Endpoints/              # IngestEndpoints, MetricsEndpoints, SetupEndpoints
└── Dtos/                   # IngestPayload/Response, MetricsDtos
api/Forge.Metrics.Tests/    # xUnit: 11 tests covering parser + attribution math
db/001_schema.sql           # reference schema (runtime uses EnsureCreated)
docker-compose.yml          # mssql + api services
scripts/
├── enrich-and-post.sh      # served at /setup/.../enrich-and-post.sh
└── setup.sh.tmpl           # rendered by /setup/{teamId}/{apiKey}
```

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

See architecture §9 (known limitations) and "Future Considerations". In particular: no row-level security, no per-developer auth, no dashboard frontend, no retry queue beyond the 3-attempt curl loop in `enrich-and-post.sh`.
