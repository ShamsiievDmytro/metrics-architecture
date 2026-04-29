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
6. Restarts the git-ai background daemon (`git-ai bg restart`) so the new hook + feature flag take effect
7. Verifies daemon is running and prints status

**Re-running is the upgrade path.** When we fix something in the enrichment script, update the wrapper, or git-ai releases a new version — developers re-run the same command. The script handles everything.

### What the script creates

```
~/.forge-ai/
├── config.json              ← { "api_url": "...", "api_key": "...", "project_root": "..." }
└── enrich-and-post.sh       ← enrichment + POST script
```

The `project_root` is auto-detected during setup (finds where `.git` directories are) or manually specified. This tells the enrichment script where to search for repos when computing diff stats.

Plus two entries in **git-ai's own config** (`~/.git-ai/config.json`, NOT git's global config — git config keys cannot contain underscores). Both are written via the `git-ai` CLI:

```bash
git-ai config --add git_ai_hooks.post_notes_updated ~/.forge-ai/enrich-and-post.sh
git-ai config set feature_flags.async_mode true          # pin: today's default, but insulate against future flips
```

`async_mode` pinning is what makes `post_notes_updated` dispatch reliably without blocking the commit — the setup script sets it explicitly so the pipeline doesn't drift if the upstream default changes.

**About `feature_flags.git_hooks_enabled` (do NOT set this).** The name is misleading. It controls git-ai's *native-git-hooks* subsystem (`git-ai install-hooks` + per-repo `.git/hooks/*` editor wrappers used by Cursor/JetBrains/etc.) — **not** the daemon's `git_ai_hooks.<event>` dispatch we rely on. Verified empirically 2026-04-29: with `git_hooks_enabled=false` and the daemon restarted, an AI-attributed commit still produced HTTP 200 ingest. Earlier iterations of this doc claimed it must be `true`; that was wrong, and the setup script no longer touches it.

**Hook command shape.** `git_ai_hooks.<event>` is a `name → command(s)` map; the value can be a single string or an array of strings. Use `set` (overwrites) on re-runs rather than `--add` (appends, duplicates). The setup script `unset`s first defensively.

### Why the setup script restarts the daemon

The git-ai background service reads `~/.git-ai/config.json` at startup and caches it in memory. Newly registered hooks (`git_ai_hooks.post_notes_updated`) and toggled feature flags (`feature_flags.async_mode`, etc.) **do not take effect until the daemon restarts**. The setup script ends with `git-ai bg restart` for this reason. Skipping the restart is the most common cause of "the script ran but my hook never fires" — observed during validation on 2026-04-28.

### How the hook is delivered (no per-repo install)

The hook is fired by the git-ai daemon, not by per-repo `.git/hooks/` stubs. Commits go through the git-ai wrapper (`~/.git-ai/bin/git`, which `git-ai install-hooks` puts ahead on `PATH` for installed coding agents). The wrapper records the commit, the daemon writes the AI note, and **then** the daemon dispatches `post_notes_updated`. As a result:

- Setup is **per-developer, global** — one `curl … | bash` covers every repo on that machine.
- No `core.hooksPath` is set on individual repos.
- `git-ai install-hooks` is part of the upstream installer; the Forge AI setup script does not need to call it per repo.

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

**Hook stdin payload shape (validated 2026-04-28).** The git-ai daemon delivers `post_notes_updated` payloads as a **JSON array** of one or more event objects, not a bare object. The script must normalize:

```bash
EVENTS="$(jq -c 'if type=="array" then . else [.] end' <<<"$PAYLOAD")"
# then iterate and POST one /api/ingest call per event
```

A real captured payload (from `~/.forge-ai/last-payload.json`):

```json
[{"branch":"main","commit_sha":"7e1ea516…","is_default_branch":true,
  "note_content":"…","repo_name":"metrics-architecture",
  "repo_url":"https://github.com/ShamsiievDmytro/metrics-architecture.git"}]
```

Fields the daemon supplies in each event: `branch`, `commit_sha`, `is_default_branch`, `note_content`, `repo_name`, `repo_url`. (Confirmed by inspecting binary symbols in `~/.git-ai/bin/git-ai` — these are not currently documented at usegitai.com.)

**Fields the script adds:**
- `diff_additions` — from `git diff --numstat` (commit-scoped, accurate)
- `diff_deletions` — from `git diff --numstat`
- `committed_at` — from `git log -1 --format=%aI` (with timezone)

**Stderr capture (operational must-have).** The git-ai daemon discards hook stderr, and the script intentionally `exit 0`s on telemetry failure to never block commits. Without explicit logging, every failure is silent. The script redirects stderr to `~/.forge-ai/last-run.log` and dumps each raw payload to `~/.forge-ai/last-payload.json`:

```bash
LOG="${HOME}/.forge-ai/last-run.log"
exec 2> >(tee -a "$LOG" >&2)
echo "=== $(date -u +%FT%TZ) hook fired (pid=$$) ===" >&2
printf '%s' "$PAYLOAD" > "${HOME}/.forge-ai/last-payload.json"
```

These two files are the only window into hook behavior — see Section 10 (Operations & Troubleshooting).

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
2. **Validate required fields** → reject 400 if `repo_name`, `commit_sha`, or `note_content` is missing/empty. (Without this, the System.Text.Json positional-record binding lets nulls slip through and `NoteParser.Parse(null)` throws NRE → 500. Hardened 2026-04-28.)
3. **Deduplicate** → if `(team_id, repo_name, commit_sha)` exists, upsert with latest data (a re-submitted note may have better attribution after git-ai update or rebase).
4. **Parse `note_content`:**
   - File map section (before `---`): file paths + line ranges, each attributed to a prompt ID or human ID (`h_` prefix)
   - JSON section (after `---`): `prompts` with per-prompt metadata, `humans` with author name. The on-the-wire shape from git-ai 1.3.4 is:
     - `prompts[id].agent_id.tool` — agent name (e.g., `claude`)
     - `prompts[id].agent_id.model` — model id (e.g., `claude-opus-4-7`)
     - `prompts[id].overriden_lines` — note the spelling: git-ai 1.3.4 misspells `overridden` as `overriden` (single `d`). Parsers must read this key.
     - `prompts[id].accepted_lines`, `total_additions`, `human_author`, etc. — additional metadata
     The flat fallback (`prompts[id].agent`, `prompts[id].model`, `prompts[id].overridden_lines`) is still accepted for forward-compatibility / synthetic test payloads.
5. **Extract author** → the daemon's top-level payload does **not** carry `commit_author`. Read `prompts.<id>.human_author` first, fall back to `humans.<id>.human_author` (pure-human commits). Only fall back to `payload.commit_author` if neither is present (e.g., synthetic test payloads). Hardened 2026-04-28 — before this fix, every real ingest had `CommitAuthor=NULL`.
6. **Compute attribution (from file map — no external data needed):**
   - `agent_lines` = sum of line ranges with prompt IDs (not `h_` prefixed)
   - `human_lines` = sum of line ranges with `h_` prefix
   - If no file map (pure human commit, `prompts: {}`): `agent_lines = 0`, `human_lines = 0` (unknown)
   - `agent_percentage` = `agent_lines` / (`agent_lines` + `human_lines`) × 100
   - If enriched `diff_additions` present: use as validation and fill `human_lines` for pure human commits
   - `overridden_lines` from prompts metadata (AI lines that human later modified)
7. **Store** in `commits` table with `team_id`.
8. **Timestamp:** use `committed_at` if enriched, otherwise `ingested_at`.

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
| Git-ai new version | Re-run setup command | Script updates git-ai + re-applies hook + restarts daemon |
| API URL changes | Re-run setup command | Config updated, daemon restarted to pick up changes |
| Git-ai fixes Codex/Cursor natively | Re-run setup command | Script detects new note format, adjusts |
| `post_notes_updated` payload adds new fields | Re-run setup command | Enrichment script updated to use native fields |
| Hook registered but not firing | Re-run setup command | Daemon restart (last step) reloads cached config |
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

### Multi-repo end-to-end (validated 2026-04-29)

Setup is global per-developer: one `~/.git-ai/config.json` + one `~/.forge-ai/enrich-and-post.sh` handles **every** repo on the machine. Verified by committing simultaneously across three repos in one Claude Code session. Hook log shows three sequential dispatches; DB has three rows with consistent attribution:

| sha | repo | author | agent | ai/hum | overridden | pct | adds | dels |
|---|---|---|---|---|---|---|---|---|
| `49741c0a3b93…` | gitai-workspace | Dmytro Shamsiiev | claude | 2/8 | 1 | 20.0 | 10 | 1 |
| `f171eea8db80…` | gitai-service-a | Dmytro Shamsiiev | claude | 2/8 | 1 | 20.0 | 10 | 1 |
| `ae20f6f29e22…` | gitai-service-b | Dmytro Shamsiiev | claude | 2/10 | 1 | 16.7 | 12 | 1 |

The three notes share prompt id `d4a7828bfdafe456` (Claude session `b190b059-…`) — useful signal that one chat session produced changes across three repos.

#### Worked example: how a `RawNote` maps to columns

Take `49741c0a3b93…` (gitai-workspace). The stored `RawNote` is:

```
test.json
  h_dca485b1adf836 7-14
  d4a7828bfdafe456 5-6
---
{
  "schema_version": "authorship/3.0.0",
  "git_ai_version": "1.3.4",
  "base_commit_sha": "49741c0a3b93…",
  "prompts": {
    "d4a7828bfdafe456": {
      "agent_id": { "tool": "claude", "model": "claude-opus-4-7", "id": "b190b059-…" },
      "human_author": "Dmytro Shamsiiev",
      "total_additions": 3, "total_deletions": 1,
      "accepted_lines": 2, "overriden_lines": 1
    }
  },
  "humans": {
    "h_dca485b1adf836": { "author": "Dmytro Shamsiiev" }
  }
}
```

Server-side derivation:
- `agent_lines` = lines 5-6 → **2** (one prompt id, two-line range)
- `human_lines` = lines 7-14 → **8** (one human id, eight-line range)
- `agent_percentage` = 2 / (2 + 8) = **20.0%**
- `overridden_lines` = `prompts[d4a7828b…].overriden_lines` = **1** (one AI line was later modified by the human)
- `commit_author` = `prompts[d4a7828b…].human_author` = **"Dmytro Shamsiiev"** (extracted; not in top-level payload)
- `agent` / `model` = `claude` / `claude-opus-4-7` (joined from contributing prompt ids)
- `diff_additions` / `diff_deletions` = **10** / **1** (added by the enrichment script via `git diff --numstat`)
- `committed_at` = `2026-04-29 08:54:51` (added by the enrichment script via `git log -1 --format=%aI`)

The same structure holds for the other two example commits — see them stored verbatim in the `RawNote` column.

### What was broken when this doc was first written, and how it was fixed (2026-04-28)

Captured here so the next person setting this up doesn't re-debug the same things:

| # | Bug | Fix | Where |
|---|---|---|---|
| 1 | API returned 500 (NRE) on payloads missing `note_content` | Reject with 400; null-guard `NoteParser.Parse` | `Parsing/NoteParser.cs`, `Endpoints/IngestEndpoints.cs` |
| 2 | **Hook payload is a JSON array**, script assumed a single object → `jq: Cannot index array with string "repo_url"` → POST never sent. Not in usegitai.com docs; confirmed by inspecting binary symbols + a live capture | Normalize: `if type=="array" then . else [.] end`, loop one POST per event | `scripts/enrich-and-post.sh` |
| 3 | Hook stderr is discarded by the daemon and the script `exit 0`s on failure → every failure invisible | Tee stderr to `~/.forge-ai/last-run.log`, dump raw payload to `~/.forge-ai/last-payload.json`, log per-attempt HTTP code + response body | `scripts/enrich-and-post.sh` |
| 4 | Top-level payload has no `commit_author` field → every real ingest had `CommitAuthor=NULL` | Extract from `prompts.<id>.human_author`, fall back to `humans.<id>.human_author` | `Endpoints/IngestEndpoints.cs` |
| 5 | Setup script didn't pin `feature_flags.async_mode` → relied on a default that could flip in a future git-ai release | `git-ai config set feature_flags.async_mode true` in setup | `scripts/setup.sh.tmpl` |

---

## Section 9: Known Limitations

**9.1 Local-only commits included.** The webhook fires on every commit, including branches that may never be pushed. Dashboard shows all commits.

**9.2 Squash/rebase not reconciled.** Squash-merged commits get new SHAs with no attribution. Original commits stay in the dashboard. Metrics measure drafts, not production code.

**9.3 Codex attribution broken in git-ai 1.3.4.** Codex commits produce `prompts: {}`. Pending fix: git-ai-project/git-ai#1204.

**9.4 Cursor attribution broken in git-ai 1.3.4.** Cursor commits don't trigger trace2 events. Pending fix: git-ai-project/git-ai#1204.

**9.5 Multi-agent conflict.** When multiple agents are active, the first agent's checkpoint claims file changes. Wrong tool may be credited.

**9.6 `post_notes_updated` hook stability.** Not prominently documented by git-ai. Behavior may change in future versions. The enrichment script abstracts this — updates deploy via re-running the setup command. Also: the daemon caches `~/.git-ai/config.json` in memory at startup, so any change to `git_ai_hooks.*` or `feature_flags.*` requires a `git-ai bg restart` before it takes effect (the setup script does this automatically; manual edits do not).

**9.7 3 retries, then data lost.** If the API is down for extended periods, commits during that time are not recovered. Acceptable for v1.

**9.8 `commit_author` is free text.** Same person on different machines with different `git config user.name` appears as different developers. Admin can manually reconcile in the database.

**9.9 100% AI attribution can over-count when work happens entirely inside an integrated AI editor.** git-ai attributes each line by *who last wrote it through an editor hook*, not by who originated the idea. If you guide every keystroke but the bytes hit disk through Claude Code's `PostToolUse` hook, the lines are tagged AI and `overriden_lines` stays 0. Human attribution requires either (a) editing in a non-AI flow with a separate git-ai integration, or (b) modifying lines that were previously AI-attributed (which then bumps `overridden_lines`). This is an upstream attribution-model limitation, not a server-side bug.

**9.10 Hook stderr is discarded by the git-ai daemon.** Combined with the script's defensive `exit 0`, this means a broken hook produces zero visible signal — commits succeed, ingest silently fails, dashboard goes flat with no error to chase. Mitigated by the script's own `~/.forge-ai/last-run.log` (see Section 10) but worth being aware of.

**9.11 Daemon caches `~/.git-ai/config.json` in memory.** Any change to `git_ai_hooks.*` or `feature_flags.*` requires `git-ai bg restart` before it takes effect. The setup script does this automatically; manual edits do not. Symptom: "I added the hook, why isn't it firing?" — answer: restart the daemon.

**9.12 Stale daemons can hold the trace2 socket.** `git-ai bg restart` should handle this, but a crash mid-restart can leave a zombie listening on `~/.git-ai/internal/daemon/trace2.sock`. Symptom: hook fires intermittently, `git-ai bg status` shows healthy. See Section 10 for the kill-it-with-fire procedure.

---

## Section 10: Operations & Troubleshooting

Everything you need to verify the pipeline is healthy and diagnose it when it isn't.

### 10.1 Health check (run anytime, ~5 seconds)

```bash
# Daemon running?
git-ai bg status
# Expected: { "ok": true, "data": { "family_key": "...", "latest_seq": <N> } }

# Hook + flags as configured?
git-ai config git_ai_hooks.post_notes_updated
git-ai config feature_flags.async_mode              # → true (only flag we pin)
git-ai config prompt_storage                        # → "local"

# API reachable?
curl -sS http://<api-host>:8000/health             # → {"status":"ok"}
```

### 10.2 End-to-end smoke test

Make a tiny AI-attributed change in any repo under `~/Projects` and commit through any path that involves the git-ai wrapper:

```bash
# 1. Clear logs so you only see this run
: > ~/.forge-ai/last-run.log
rm -f ~/.forge-ai/last-payload.json /tmp/forge-ai-resp.json

# 2. (Have an AI agent edit a file, then) commit
git commit -am "smoke test"

# 3. Watch the hook log (should appear within ~3 seconds)
cat ~/.forge-ai/last-run.log
# Expected:
#   === <ts> hook fired (pid=...) ===
#   [forge-ai] events=1
#   [forge-ai] ok http=200 commit=<sha>

# 4. Confirm the row landed
docker exec <mssql> /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PASS" -C -d forge_ai_metrics \
  -Q "SELECT TOP 1 LEFT(CommitSha,12), CommitAuthor, Agent, AgentLines, HumanLines, AgentPercentage \
      FROM Commits ORDER BY IngestedAt DESC"
```

If any step fails, walk down the next subsections in order.

### 10.3 The two diagnostic files (your only window into the hook)

| File | Written by | Tells you |
|---|---|---|
| `~/.forge-ai/last-run.log` | The hook script's stderr (tee'd) | Whether the hook fired, how many events, HTTP code per attempt, final outcome |
| `~/.forge-ai/last-payload.json` | The hook script (every run, overwrites) | The exact JSON the daemon sent — useful when payload shape changes in a new git-ai release |

**Replay a captured payload manually** (bypasses the daemon, isolates script-vs-API problems):

```bash
bash -x ~/.forge-ai/enrich-and-post.sh < ~/.forge-ai/last-payload.json 2>&1 | tail -40
```

If this works but live commits don't, the daemon isn't dispatching. If this also fails, the script or API is the issue.

### 10.4 Daemon problems

**Daemon log location:** `~/.git-ai/internal/daemon/logs/<pid>.log`. Find the active one:

```bash
PID=$(jq -r '.pid' ~/.git-ai/internal/daemon/daemon.pid.json)
tail -f ~/.git-ai/internal/daemon/logs/${PID}.log
```

You should see `INFO checkpoint start/done` lines for every `git commit` and `INFO git write op completed op="commit"` for the commit itself. Hook dispatch events go to stderr with `[git_ai_hooks]` prefix and only show on errors — silence here is normal.

**Restart the daemon** (after any `~/.git-ai/config.json` change, manual or otherwise):

```bash
git-ai bg restart
```

### 10.5 Zombie daemon (multiple processes, stale socket)

Symptoms:
- `git-ai bg status` reports OK but hooks fire intermittently or never
- `~/.forge-ai/last-run.log` empty across multiple commits despite checkpoints in the daemon log
- Two PIDs in the daemon's logs/ directory with overlapping timestamps
- `lsof ~/.git-ai/internal/daemon/trace2.sock` shows multiple owners

Recovery:

```bash
# 1. List all running daemons
ps -ef | grep -E "git-ai (bg|daemon)" | grep -v grep

# 2. Stop cleanly
git-ai bg stop

# 3. If `bg stop` doesn't kill them all, force-kill remaining PIDs
for pid in $(pgrep -f "git-ai (bg|daemon)"); do kill -9 "$pid"; done

# 4. Remove stale runtime state (lock + sockets)
rm -f ~/.git-ai/internal/daemon/daemon.lock \
      ~/.git-ai/internal/daemon/control.sock \
      ~/.git-ai/internal/daemon/trace2.sock

# 5. Start fresh
git-ai bg start

# 6. Verify only ONE daemon
ps -ef | grep -E "git-ai (bg|daemon)" | grep -v grep | wc -l   # → 1
git-ai bg status
```

Re-run the smoke test (10.2) after recovery.

### 10.6 Hook fires but POST fails

Inspect `~/.forge-ai/last-run.log` for the per-attempt line:

```
[forge-ai] attempt=1 http=400 commit=<sha> body={"error":"..."}
```

| HTTP | Likely cause | Action |
|---|---|---|
| 000 / connection refused | API not running, wrong `api_url` | `curl /health`, fix `~/.forge-ai/config.json` |
| 401 | API key mismatch | Re-run setup with the correct `API_KEY` |
| 400 | Missing required field, malformed note | Inspect `last-payload.json`, replay manually |
| 500 | Server bug or DB down | Check API container logs, check MSSQL container health |

The script retries 3× with 2s backoff before giving up.

### 10.7 API server problems

```bash
# Tail API logs (the parser/EF stack will print exceptions here)
docker logs --tail=200 -f <api-container>

# Reproduce against the live endpoint
curl -sS -X POST http://<api>:8000/api/ingest \
     -H "Content-Type: application/json" \
     -H "X-API-Key: $API_KEY" \
     -d @~/.forge-ai/last-payload.json -w "\nHTTP=%{http_code}\n"
```

### 10.8 DB queries for verification

```sql
-- Recent ingests (last hour)
SELECT TOP 20 LEFT(CommitSha,12), RepoName, CommitAuthor, Agent, AgentLines,
              HumanLines, AgentPercentage, FORMAT(IngestedAt,'HH:mm:ss')
FROM Commits
WHERE IngestedAt > DATEADD(hour, -1, SYSUTCDATETIME())
ORDER BY IngestedAt DESC;

-- Per-team totals
SELECT t.Name, COUNT(*) AS commits,
       SUM(c.AgentLines) AS ai_lines, SUM(c.HumanLines) AS human_lines
FROM Commits c JOIN Teams t ON t.Id = c.TeamId
GROUP BY t.Name;

-- Authors that show <null> (suggest the human_author extraction missed)
SELECT LEFT(CommitSha,12), RepoName, FORMAT(IngestedAt,'yyyy-MM-dd HH:mm')
FROM Commits WHERE CommitAuthor IS NULL ORDER BY IngestedAt DESC;
```

### 10.9 Re-running the developer setup is always safe

The setup script is fully idempotent: it `unset`s before `set`ting hook entries, `git-ai upgrade` no-ops on the latest version, and `bg restart` works whether or not a daemon is already running. **When in doubt, re-run setup** — it's the documented upgrade path.

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
