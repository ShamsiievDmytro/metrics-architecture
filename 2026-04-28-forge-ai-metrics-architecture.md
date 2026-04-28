# Forge AI Metrics Platform — Architecture Design

**Date:** 2026-04-28
**Status:** Production v1
**Goal:** Multi-tenant platform that collects AI coding attribution from developer machines and provides metrics dashboards per team — with minimal setup, no SCM integration, and no repository access.

---

## Architecture Overview

```
Developer Machine                     Forge AI Metrics Platform
┌────────────────────────┐           ┌──────────────────────────┐
│                        │           │                          │
│  AI Agent edits code   │           │  Ingest API              │
│  git-ai creates note   │  HTTP POST│  ├── validate API key    │
│  on commit             │──────────▶│  ├── parse note          │
│                        │ (enriched │  ├── compute AI/human    │
│  post_notes_updated    │  payload) │  └── store in MS SQL     │
│  hook fires            │           │                          │
│  enrich-and-post.sh    │           │  Metrics API             │
│  ├── adds diff stats   │           │  ├── summary             │
│  ├── adds commit date  │           │  ├── by-agent            │
│  └── POSTs with retries│           │  ├── by-developer        │
│                        │           │  └── by-repo             │
└────────────────────────┘           │                          │
     per developer                   │  MS SQL Server           │
                                     └──────────────────────────┘
┌────────────────────────┐                    │
│  Dashboard (React)     │◀───────────────────┘
│  per-team views        │       REST API
└────────────────────────┘
```

### Data Flow

1. Developer commits with AI agent → git-ai daemon creates note locally
2. Daemon fires `post_notes_updated` hook → `enrich-and-post.sh` runs
3. Script adds `diff_additions`, `diff_deletions`, `committed_at` from local git
4. Script POSTs enriched payload to Forge AI API (with team API key, 3 retries)
5. API parses note, computes AI/human split, stores in MS SQL
6. Dashboard queries API, renders per-team metrics

### What This Does NOT Require

- No SCM integration
- No repository cloning on the server
- No CI/CD changes
- No per-repo configuration
- No push refspecs

---

## Section 1: Developer Setup

### Idempotent setup script

One command per developer. Safe to re-run on updates.

```bash
curl -s https://forge-ai-metrics.company.com/setup/TEAM_ID/API_KEY | bash
```

The script is idempotent — it:
1. Installs git-ai if missing, skips if already installed
2. Updates git-ai if a newer version is available
3. Deploys `enrich-and-post.sh` to `~/.forge-ai/` (always overwrites with latest)
4. Sets `post_notes_updated` hook (replaces if exists, preserves other git-ai config)
5. Sets `prompt_storage local` (idempotent)
6. Verifies setup and prints status

**Re-running is the upgrade path.** When we fix something in the enrichment script, update the wrapper, or git-ai releases a new version — developers re-run the same command. The script handles everything.

### What the script creates

```
~/.forge-ai/
├── config.json              ← { "api_url": "...", "api_key": "...", "project_root": "..." }
└── enrich-and-post.sh       ← enrichment + POST script
```

The `project_root` is auto-detected during setup (finds where `.git` directories are) or manually specified. This tells the enrichment script where to search for repos when computing diff stats.

Plus one git-ai config entry:
```
git_ai_hooks.post_notes_updated = ~/.forge-ai/enrich-and-post.sh
```

### How attribution is computed

The `note_content` in git-ai's payload contains a **file map** that lists every changed line and who wrote it:

```
calc-test-ai.json
  h_dca485b1adf836 11-13        ← human lines (h_ prefix)
  33d7da781a966cb5 9-10          ← AI lines (prompt ID)
human-only.json
  h_dca485b1adf836 1-3           ← human lines
```

The server computes:
- `agent_lines` = sum of line ranges with prompt IDs (not `h_` prefixed) → 2
- `human_lines` = sum of line ranges with `h_` prefix → 6
- `agent_percentage` = 2 / (2 + 6) = 25%

**No external data needed for mixed commits.** The file map covers both AI and human lines with their exact ranges. The `total_additions` field in the JSON metadata is session-scoped (unreliable for per-commit math) — ignore it and use the file map instead.

**Exception: pure human commits.** When a commit has `prompts: {}` and no file map, all lines are human but the server doesn't know how many. For these, the server records `agent_lines = 0`, `human_lines = 0` (unknown), `agent_percentage = 0`. The commit is counted but line totals exclude it. This is acceptable — the metric that matters is "how much AI code" not "how much human code."

### Enrichment script (for pure human commits and timestamps)

For commits with AI activity, the file map in `note_content` provides all line counts — no enrichment needed. For **pure human commits** (`prompts: {}`, no file map), the server needs `diff_additions` to know how many lines were changed. Additionally, `committed_at` provides accurate timestamps for time-series charts.

The enrichment script runs between the git-ai hook and the API POST, adding fields from local git.

**Fields added:**
- `diff_additions` — from `git diff --numstat` (commit-scoped, accurate)
- `diff_deletions` — from `git diff --numstat`
- `committed_at` — from `git log -1 --format=%aI` (with timezone)

**How the script finds the local repo:** The `post_notes_updated` hook runs from the git-ai daemon's CWD (`~/.git-ai/internal/daemon/`), not from the repo. The setup script registers the developer's **project root** in `~/.forge-ai/config.json`. The enrichment script searches only that directory:

```json
{
  "api_url": "https://forge-ai-metrics.company.com/api/ingest",
  "api_key": "fai_abc123",
  "project_root": "/Users/dmytro/Projects"
}
```

The script matches `repo_url` from the payload against `remote.origin.url` in repos under `project_root`. This is:
- **Fast** — searches one directory tree, not the entire filesystem
- **Deterministic** — set once during setup, no heuristics
- **Cross-platform** — absolute path works on macOS, Linux, Windows (Git Bash)
- **Auto-detected** — setup script finds the project root by locating `.git` directories

If the repo is not found (moved/deleted), enrichment fields are empty but the POST still succeeds with file-map-based attribution.

**If enrichment is not used:** The server computes AI/human split from the file map (accurate for mixed and AI commits). Pure human commits show `agent_percentage = 0` without line counts. `ingested_at` is used instead of `committed_at` (typically within seconds of commit).

**Pending upstream request:** Asked git-ai to include `diff_additions`, `diff_deletions`, `committed_at` in the native `post_notes_updated` payload. If accepted, enrichment becomes unnecessary entirely — the script reduces to a simple curl POST.

**Retry:** 3 attempts with 2-second delay. If all fail, payload is lost (acceptable for v1).

---

## Section 2: Ingest API

### Endpoint

```
POST /api/ingest
Headers:
  X-API-Key: <team_api_key>
  Content-Type: application/json
```

### Processing

1. **Validate API key** → resolve `team_id`. Reject 401 if invalid.
2. **Deduplicate** → if `(team_id, repo_name, commit_sha)` exists, upsert with latest data (a re-submitted note may have better attribution after git-ai update or rebase).
3. **Parse `note_content`:**
   - File map section (before `---`): file paths + line ranges, each attributed to a prompt ID or human ID (`h_` prefix)
   - JSON section (after `---`): prompts with agent/model/accepted/overridden, humans with author name
4. **Compute attribution (from file map — no external data needed):**
   - `agent_lines` = sum of line ranges with prompt IDs (not `h_` prefixed)
   - `human_lines` = sum of line ranges with `h_` prefix
   - If no file map (pure human commit, `prompts: {}`): `agent_lines = 0`, `human_lines = 0` (unknown)
   - `agent_percentage` = `agent_lines` / (`agent_lines` + `human_lines`) × 100
   - If enriched `diff_additions` present: use as validation and fill `human_lines` for pure human commits
   - `overridden_lines` from prompts metadata (AI lines that human later modified)
5. **Store** in `commits` table with `team_id`.
6. **Timestamp:** use `committed_at` if enriched, otherwise `ingested_at`.

### Attribution calculation — validated example

Mixed commit with AI + human edits across 2 files:

```
File map (from note_content):
  calc-test-ai.json
    h_dca485b1adf836 11-13      ← human: 3 lines
    33d7da781a966cb5 9-10        ← AI: 2 lines
  human-only.json
    h_dca485b1adf836 1-3         ← human: 3 lines

Server calculation (from file map alone, no enrichment needed):
  agent_lines = 2 (lines 9-10)
  human_lines = 6 (lines 11-13 + lines 1-3)
  agent_percentage = 2 / (2+6) = 25%
  overridden_lines = 1 (from prompts JSON)

Validation with enriched diff_additions = 8:
  agent_lines + human_lines = 2 + 6 = 8 ✓ matches diff_additions
```

Cross-repo sibling with same mixed edits:

```
File map:
  calc-test-sibling.json
    h_dca485b1adf836 7-8         ← human: 2 lines
    33d7da781a966cb5 5-6          ← AI: 2 lines

Server calculation:
  agent_lines = 2
  human_lines = 2
  agent_percentage = 2 / (2+2) = 50%
  overridden_lines = 1

Validation with enriched diff_additions = 4:
  agent_lines + human_lines = 2 + 2 = 4 ✓ matches diff_additions
```

Pure human commit (no file map):

```
Note: prompts: {}, no file map section

Server calculation:
  agent_lines = 0
  human_lines = 0 (unknown — no file map to count)
  agent_percentage = 0%
  
With optional enrichment: human_lines = diff_additions (all human)
```

### Idempotency

Upsert on `(team_id, repo_name, commit_sha)`. Same commit re-submitted updates the row with latest data. Safe for retries, script re-runs, git-ai note rewrites.

---

## Section 3: Database Schema

Single MS SQL table, multi-tenant via `team_id`.

```sql
CREATE TABLE teams (
  id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
  name NVARCHAR(255) NOT NULL,
  api_key_hash NVARCHAR(255) NOT NULL,
  created_at DATETIME2 DEFAULT GETUTCDATE()
);

CREATE TABLE commits (
  id UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
  team_id UNIQUEIDENTIFIER NOT NULL REFERENCES teams(id),
  repo_name NVARCHAR(500) NOT NULL,
  repo_url NVARCHAR(1000),
  commit_sha NVARCHAR(40) NOT NULL,
  branch NVARCHAR(500),
  is_default_branch BIT DEFAULT 0,
  commit_author NVARCHAR(255),       -- human_author from note (free text)
  agent NVARCHAR(100),               -- claude, github-copilot, codex, cursor, none
  model NVARCHAR(255),               -- claude-opus-4-7, gpt-5.4
  agent_lines INT NOT NULL DEFAULT 0,
  human_lines INT NOT NULL DEFAULT 0,
  overridden_lines INT NOT NULL DEFAULT 0,
  agent_percentage DECIMAL(5,1) NOT NULL DEFAULT 0,
  diff_additions INT NOT NULL DEFAULT 0,
  diff_deletions INT NOT NULL DEFAULT 0,
  committed_at DATETIME2,            -- actual commit time (enriched)
  ingested_at DATETIME2 DEFAULT GETUTCDATE(),
  raw_note NVARCHAR(MAX),
  CONSTRAINT UQ_commit UNIQUE(team_id, repo_name, commit_sha)
);

CREATE INDEX IX_commits_team ON commits(team_id);
CREATE INDEX IX_commits_repo ON commits(team_id, repo_name);
CREATE INDEX IX_commits_author ON commits(team_id, commit_author);
CREATE INDEX IX_commits_date ON commits(team_id, committed_at);
CREATE INDEX IX_commits_agent ON commits(team_id, agent);
```

**Why flat table:** Multi-agent commits (Claude + Copilot in one commit) are rare in practice. When they occur, `agent` stores the comma-separated list (same as PoC). If multi-agent tracking becomes important, normalize into `commit_agents` table later — the `raw_note` column preserves all original data for replay.

---

## Section 4: Metrics API

```
GET /api/metrics/summary?period=30d
  → { total_commits, ai_commits, ai_percentage, total_ai_lines, total_human_lines }

GET /api/metrics/by-agent?period=30d
  → [{ agent, commits, ai_lines, avg_percentage }]

GET /api/metrics/by-developer?period=30d
  → [{ author, commits, ai_percentage, ai_lines }]

GET /api/metrics/by-repo?period=30d
  → [{ repo_name, commits, ai_percentage, ai_lines }]
```

All endpoints filter by `team_id` (from API key). Period uses `committed_at` (actual commit time, not ingestion time).

**Period options:** 7d, 30d, 90d, custom range.

---

## Section 5: Tenant Onboarding

**1. Admin creates team** (internal, manual for now):
```sql
INSERT INTO teams (name, api_key_hash) VALUES ('Platform Team', '<hash>');
```

**2. Admin sends to team lead:**
- API key
- Setup command: `curl -s https://forge-ai-metrics.company.com/setup/TEAM_ID/API_KEY | bash`

**3. Each developer runs the setup command.** 2 minutes. Done.

**4. First AI commit → data appears in dashboard.**

| Step | Who | Time |
|---|---|---|
| Create team | Admin | 2 minutes |
| Developer setup | Each developer | 2 minutes |
| First data | Automatic | Next AI commit |

---

## Section 6: Idempotency and Upgrades

The entire system is designed around **re-runnability**:

### Developer-side

| Change | Developer action | What happens |
|---|---|---|
| Enrichment script updated | Re-run setup command | Script overwritten with latest |
| Git-ai new version | Re-run setup command | Script updates git-ai + re-applies hook |
| API URL changes | Re-run setup command | Config updated |
| Git-ai fixes Codex/Cursor natively | Re-run setup command | Script detects new note format, adjusts |
| `post_notes_updated` payload adds new fields | Re-run setup command | Enrichment script updated to use native fields |
| Developer changes machine | Run setup command on new machine | Fresh install |

### Server-side

| Change | What happens |
|---|---|
| Same commit re-submitted | Upsert — latest data wins |
| Note rewritten after rebase | Upsert — updated attribution |
| Git-ai version upgrade changes note format | Parser updated on server, re-ingestion from `raw_note` |
| New fields needed from notes | Parse from `raw_note` column (all original data preserved) |

### The `raw_note` column

Every ingest stores the original `note_content` verbatim. This means:
- If the parser has a bug, fix the parser and recompute from `raw_note`
- If new fields are added to the note format, extract from existing `raw_note` data
- No data is lost even if the initial parse is incomplete

---

## Section 7: Security

### What the server receives

- Commit SHA, branch, repo name/URL (metadata only)
- File paths and line numbers (no file content)
- Agent tool name and model
- Author name (from git config)
- Line counts and diff stats (no actual diff content)

### What the server does NOT receive

- Source code
- Prompts or AI responses
- Full diffs
- SCM credentials

### Multi-tenant isolation

- `team_id` on every query (application-level)
- API key resolves to exactly one team

---

## Section 8: Validated Results

The enrichment pipeline was tested end-to-end on 2026-04-28. All payloads received via webhook.site.

### Test 1: Pure AI commit (same-repo)
- Commit in `entire-poc-workspace`, file edited by Claude
- Result: `diff_additions: 4`, file map lines 6-9 = 4 AI lines, **AI% = 100%**
- `committed_at` correct with timezone

### Test 2: Pure AI commit (cross-repo sibling)
- Commit in `entire-poc-backend`, file edited by Claude from workspace
- Result: `diff_additions: 6`, file map lines 1-6 = 6 AI lines, **AI% = 100%**
- Cross-repo enrichment found the repo by matching `repo_url`

### Test 3: Mixed AI + human commit (same-repo)
- 2 files: `calc-test-ai.json` (AI + human edits) + `human-only.json` (pure human)
- Result: `diff_additions: 8`, AI lines = 2, human lines = 6, **AI% = 25%**
- `overridden_lines: 1` correctly detected (human modified an AI line)
- Human-only file correctly attributed to human (`h_` prefix in file map)

### Test 4: Mixed AI + human commit (cross-repo sibling)
- 1 file in `entire-poc-backend`: AI added 2 lines, human added 2 lines
- Result: `diff_additions: 4`, AI lines = 2, human lines = 2, **AI% = 50%**
- `overridden_lines: 1` correctly detected

### Key validation points
- `diff_additions` is commit-scoped (from `git diff --numstat`), not session-scoped
- `committed_at` includes timezone (ISO 8601 format)
- File map distinguishes AI lines (prompt ID) from human lines (`h_` prefix)
- Cross-repo enrichment works by matching `repo_url` against local git remotes
- `overridden_lines` tracked when human modifies AI-written lines before committing

---

## Section 9: Known Limitations

**9.1 Local-only commits included.** The webhook fires on every commit, including branches that may never be pushed. Dashboard shows all commits.

**9.2 Squash/rebase not reconciled.** Squash-merged commits get new SHAs with no attribution. Original commits stay in the dashboard. Metrics measure drafts, not production code.

**9.3 Codex attribution broken in git-ai 1.3.4.** Codex commits produce `prompts: {}`. Pending fix: git-ai-project/git-ai#1204.

**9.4 Cursor attribution broken in git-ai 1.3.4.** Cursor commits don't trigger trace2 events. Pending fix: git-ai-project/git-ai#1204.

**9.5 Multi-agent conflict.** When multiple agents are active, the first agent's checkpoint claims file changes. Wrong tool may be credited.

**9.6 `post_notes_updated` hook stability.** Not prominently documented by git-ai. Behavior may change in future versions. The enrichment script abstracts this — updates deploy via re-running the setup command.

**9.7 3 retries, then data lost.** If the API is down for extended periods, commits during that time are not recovered. Acceptable for v1.

**9.8 `commit_author` is free text.** Same person on different machines with different `git config user.name` appears as different developers. Admin can manually reconcile in the database.

---

## Future Considerations

These are NOT in v1 but the architecture supports adding them:

- **Per-developer tokens** — replace shared API key with per-developer identity
- **Contributor aliasing** — merge different author names into canonical identity
- **Repository normalization** — `repos` table with URL canonicalization
- **Normalized schema** — `commit_agents`, `commit_files` child tables for multi-agent commits
- **CI reconciliation** — handle squash/rebase merges
- **Local fallback queue** — persist failed POSTs for retry
- **SCM integration** — webhook-based ingestion for enterprise tenants
- **Row-level security** — database-enforced tenant isolation
- **Health monitoring** — detect silent telemetry loss
- **Backfill** — import historical notes from repos
