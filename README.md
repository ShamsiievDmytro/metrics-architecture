# Forge AI Metrics — POC (.NET 10 + EF Core)

End-to-end working POC: a developer commits with an integrated AI agent (Claude Code, Copilot) → git-ai's daemon attributes the change → our hook posts metadata to a multi-tenant ingest API → MS SQL stores it → metrics endpoints expose it.

## Documentation

| Doc | Audience | Purpose |
|---|---|---|
| [`docs/forge-architecture-overview.md`](./docs/forge-architecture-overview.md) | Tech leadership / cross-team review | High-level overview, diagrams, tenant-onboarding flow, honest limitations. **Start here.** |
| [`docs/onboarding.md`](./docs/onboarding.md) | New developer joining a tenant | 2-minute setup, smoke test, troubleshooting recipes |
| [`docs/ai-code-metrics-architecture.md`](./docs/ai-code-metrics-architecture.md) | Engineers implementing / operating the system | Deep-dive: hook payload shape, parser, idempotency, full Section 10 troubleshooting playbook |

This README covers the **POC implementation only** — how to run, test, and probe the local stack.

## What's inside

- **MS SQL Server 2022** (Docker — `mcr.microsoft.com/mssql/server:2022-latest`)
- **ASP.NET Core 10** minimal-APIs backend with **EF Core 10**
- **Swagger UI** at `/swagger` via `Swashbuckle.AspNetCore`
- **`GET /setup/{teamId}/{apiKey}`** that emits a one-command bash installer (installs `git-ai` + `jq`, registers the `post_notes_updated` hook, writes `~/.forge-ai/config.json`)
- **`enrich-and-post.sh`** hook: handles the JSON-array payload git-ai actually sends, enriches with diff stats + `committed_at`, retries 3× then drops, captures stderr to `~/.forge-ai/last-run.log` for diagnosis
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
1. Installs `jq` (brew/apt/dnf/yum/pacman/apk) and `git-ai` (`https://usegitai.com/install.sh`) if missing.
2. On re-run, runs `git-ai upgrade` (no-op when current).
3. Auto-detects `project_root` (first of `~/Projects`, `~/Code`, `~/work`, `~/src`, `~/dev` containing a `.git` dir; override with `FORGE_PROJECT_ROOT`).
4. Downloads the latest `enrich-and-post.sh` from the API into `~/.forge-ai/`.
5. Writes `~/.forge-ai/config.json` (`api_url`, `api_key`, `team_id`, `project_root`).
6. `git-ai config --add git_ai_hooks.post_notes_updated <path>` — registers the hook in git-ai's own config (NOT git's global config).
7. `git-ai config set feature_flags.async_mode true` — pinned defensively against future default flips.
8. `git-ai config set prompt_storage local`.
9. `git-ai bg restart` — required because the daemon caches config in memory at startup.

> The script deliberately does **not** set `feature_flags.git_hooks_enabled` — that flag controls git-ai's *native-git-hooks* subsystem (`git-ai install-hooks`, per-repo `.git/hooks/*` editor wrappers used by Cursor/JetBrains), not the daemon's `git_ai_hooks.post_notes_updated` dispatch we rely on. Verified empirically 2026-04-29.

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
api/Forge.Metrics.Tests/    # xUnit: 12 tests covering parser + attribution math
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

## Verified end-to-end

Smoke-tested across multiple repos, single Claude Code session — every commit landed in MS SQL with correct attribution within ~3 seconds of `git commit`. Log of the live runs lives in `~/.forge-ai/last-run.log` on the developer machine; raw daemon payload of the most recent commit in `~/.forge-ai/last-payload.json`. See [`docs/onboarding.md`](./docs/onboarding.md) for the recipe and [`docs/ai-code-metrics-architecture.md`](./docs/ai-code-metrics-architecture.md) Section 10 for the full troubleshooting playbook.

## What this POC does NOT do (yet)

See [`docs/forge-architecture-overview.md`](./docs/forge-architecture-overview.md) "Honest limitations" section for the complete list. The most relevant gaps to flag:

- **No dashboard frontend** — read endpoints exist, no UI yet (the production integration plan reuses the existing Forge metrics dashboard).
- **No platform-level multi-tenancy** — POC keeps tenants in its own `Teams` table; production uses Forge's existing tenant-provisioning process.
- **POC API paths are `/api/ingest` + `/api/metrics/*`** — production integration moves these under `/api/coding-metrics/*` inside Forge API.
- **No retry queue beyond the 3-attempt curl loop** — extended outage = lost events. Local fallback queue is on the v2 list.
- **Cursor / Codex agents not yet covered** — pending git-ai upstream fix at [#1204](https://github.com/git-ai-project/git-ai/issues/1204).
- **No per-developer auth or row-level security** — application-level filtering by `team_id` only.

