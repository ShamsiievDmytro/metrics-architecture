# Forge AI Metrics — Coding Metrics Extension


This document describes how we extend our existing **Forge AI platform** (API, DB, metrics dashboard) and our existing **WoD AI SSD** developer setup package to capture **AI coding-attribution** metrics from every developer machine — internal and tenant.

---

## 1 — The problem

- AI coding agents (Claude, Copilot, Cursor, Codex, …) now author a meaningful share of our committed code
- Leadership wants to know **how much**, **by whom**, **with which agent/model**, **per team and per repo** — for both internal teams and tenant clients

**We need:** cross-agent, cross-repo visibility with **zero per-repo setup** and **no source code leaving the developer's machine** — and we want it integrated into the platform we already operate, not as a separate product.

---

## 2 — Our solution (extend what we already have)

We **bundle** local capture into our existing developer onboarding package, and **extend** our existing Forge platform to serve the data:

1. **Developer-side capture lives inside the WoD AI SSD setup package** — the same package developers already run for AI workflow setup. It now also installs/upgrades [git-ai](https://usegitai.com), registers a `post_notes_updated` hook, and writes the Forge AI Metrics config. **Zero new install steps for developers — it's part of the bundle.**
2. **Local attribution via [git-ai](https://usegitai.com/docs/cli)'s daemon** — git-ai already attributes every commit to AI prompt IDs vs. human author IDs at line-range granularity. Our hook script reads its output, enriches with diff stats, and posts to the platform.
3. **Ingest + read endpoints are extensions of the existing Forge API** — adds `/api/coding-metrics/ingest` and `/api/coding-metrics/*` read endpoints. Multi-tenancy support is being added to Forge API as a platform-level capability; this feature uses the same `X-API-Key` → `team_id` resolution.
4. **Storage is an extension of the existing Forge DB** — one new `Commits` table; tenant resolution leans on the platform-level multi-tenancy work (we don't ship our own tenant table). Multi-tenant isolation enforced at the application layer (every query filters on `team_id`).
5. **Visualization is an extension of the existing Forge metrics dashboard** — a new "Coding metrics" view next to existing dashboards. Same auth, same tenant scoping, same operational model.
6. **End-to-end idempotent** — re-running the package is the upgrade path for everything (git-ai version, hook script, config drift, key rotation). 

Validated end-to-end across multiple repos in one chat session — three commits to three different repos all flowed through to the dashboard within seconds.

---

## 3 — Architecture diagram

```mermaid
flowchart TB
    subgraph DIST["📦 Distribution"]
        SSD["<b>WoD AI SSD setup package</b><br/>(internal + tenant developers)<br/>installs git-ai · registers hook<br/>activates on TEAM_ID + API_KEY config"]
    end

    subgraph DEV["💻 Developer machine"]
        direction TB
        AGENT["<b>AI coding agent</b><br/>Claude · Copilot · Continue · …"]
        GITAI["<b>git-ai daemon</b> (local)<br/>trace2 + checkpoints<br/>writes refs/notes/ai"]
        HOOK["<b>enrich-and-post.sh</b><br/>post_notes_updated hook<br/>+ diff stats + retries"]
        AGENT -- "edits files" --> GITAI
        GITAI -- "fires hook<br/>(JSON array)" --> HOOK
    end

    subgraph FORGE["☁️ Forge AI platform <i>(existing, extended)</i>"]
        direction TB
        API["<b>Forge API</b><br/>+ /api/coding-metrics/* <i>(new — ingest + read)</i><br/>multi-tenancy: X-API-Key → team_id"]
        DB[("<b>Forge DB</b> <i>(existing)</i><br/>+ Commits <i>(new table)</i>")]
        UI["<b>Forge metrics dashboard</b> <i>(existing)</i><br/>+ Coding metrics view <i>(new)</i><br/>per-team · per-developer · per-repo · per-agent"]
        API --> DB
        DB --> UI
    end

    SSD ==>|"developer runs<br/>once · 2 min · idempotent"| DEV
    HOOK ==>|"HTTPS POST<br/>metadata only<br/>(no source · no prompts · no diffs)"| API

    classDef dist fill:#fef3c7,stroke:#92400e,stroke-width:2px,color:#000
    classDef dev fill:#dbeafe,stroke:#1e40af,stroke-width:2px,color:#000
    classDef forge fill:#dcfce7,stroke:#14532d,stroke-width:2px,color:#000
    class DIST dist
    class DEV dev
    class FORGE forge
```

The three layers map to **distribution → capture → platform**, top-to-bottom. Everything in green is reuse-and-extend of what we already operate.

---

## 4 — Data flow (six steps)

1. Developer's AI agent edits files → git-ai's daemon checkpoints the change against a prompt ID
2. Developer runs `git commit` → daemon writes a `refs/notes/ai` note containing the file map (line ranges per prompt or human ID) + a JSON section with prompt metadata (`agent_id.tool`, `agent_id.model`, `human_author`, `accepted_lines`, `overriden_lines`)
3. Daemon dispatches `post_notes_updated` (JSON array of events) → our hook script
4. Script enriches each event with `diff_additions`, `diff_deletions`, `committed_at`; POSTs to the new `/api/coding-metrics/ingest` endpoint on Forge API
5. Forge API authenticates via `X-API-Key` → resolves `team_id` → parses note → computes `ai_lines / human_lines / agent / model / human_author / overridden_lines` → upserts on `(team_id, repo_name, commit_sha)` into the new `Commits` table in Forge DB
6. Forge dashboard's new "Coding metrics" view queries the same Forge DB; data appears within ~3 seconds of commit

**Failure-mode policy:**
- Each event is POSTed with **3 retries, 2-second backoff**. After three failures, the event is dropped and the script exits 0 — `git commit` is **never blocked** by telemetry.
- Stderr from every hook run is tee'd to `~/.forge-ai/last-run.log` (per-attempt HTTP code + response body). The raw daemon payload of the most recent run is dumped to `~/.forge-ai/last-payload.json`.

---

## 5 — Developer local machine (what the package installs)

What ends up on the developer's machine after the WoD AI SSD package runs (or after they configure their tenant key on an already-installed package). All paths are global per-user — no per-repo state, no `.git/hooks/` symlinks.

### Files & directories

```
~/.git-ai/                              ← managed by git-ai itself
├── bin/{git-ai,git,git-og}              git-ai binaries (also wraps `git` on PATH)
├── libexec/git-core/                    trace2 plumbing
├── config.json                          git-ai config (we write hook + flags here)
└── internal/daemon/
    ├── daemon.pid.json · daemon.lock    runtime state
    ├── control.sock · trace2.sock       UNIX sockets for IPC + git event capture
    └── logs/<pid>.log                   structured daemon log

~/.forge-ai/                             ← our package adds these
├── config.json                          tenant config (api_url, api_key, team_id, project_root)
├── enrich-and-post.sh                   the post_notes_updated hook script
├── last-run.log                         stderr from each hook run (diagnosis)
└── last-payload.json                    most recent raw daemon payload (diagnosis)
```

### Configuration written

- `~/.git-ai/config.json` — set by the package via `git-ai config set …`:
  - `git_ai_hooks.post_notes_updated` → `~/.forge-ai/enrich-and-post.sh`
  - `feature_flags.async_mode` → `true` *(pinned defensively against future default flips)*
  - `prompt_storage` → `local`
  - We deliberately **do not** touch `feature_flags.git_hooks_enabled` — that flag controls git-ai's *native-git-hooks* subsystem (`git-ai install-hooks`, `.git/hooks/*` editor wrappers), not the daemon's internal `git_ai_hooks.*` dispatch we rely on. Verified empirically 2026-04-29.
- `~/.gitconfig` (global) — set by the upstream git-ai installer, **not** per-repo:
  - `trace2.eventtarget` → `af_unix:stream:.../trace2.sock`
  - `trace2.eventnesting` → `10`
- **No per-repo `.git/hooks/` symlinks. No `core.hooksPath` modifications.** This is what makes the setup zero-friction across N repos.

### Hooks registered

- `git_ai_hooks.post_notes_updated` — fires inside the git-ai daemon **after** it writes a `refs/notes/ai` note for the just-finished commit. Payload arrives as JSON on stdin (an array of one event per just-written note).

### Background services

- **git-ai daemon** — long-lived background process, controlled via `git-ai bg start/stop/restart/status`. Owns the trace2 socket. Caches `~/.git-ai/config.json` in memory at startup, so the package restarts it after writing config.

### System dependencies

| Tool | Purpose | How the package handles it |
|---|---|---|
| `curl`, `git` | baseline | refused if missing — developer must install |
| `jq` | parsing hook payloads | auto-installed via `brew` / `apt` / `dnf` / `yum` / `pacman` / `apk` |
| `git-ai` 1.3.4+ | local attribution daemon | auto-installed via official installer at [https://usegitai.com/docs/cli](https://usegitai.com/docs/cli) |


### Inputs the package needs (from admin / detection)

| Input | Source | Stored in |
|---|---|---|
| `TEAM_ID` | Forge AI Admin → tenant lead | `~/.forge-ai/config.json` |
| `API_KEY` (e.g. `acme_<random>`) | Forge AI Admin → tenant lead | `~/.forge-ai/config.json` *(plain text locally; hashed server-side)* |
| `api_url` | Package default per environment | `~/.forge-ai/config.json` |
| `project_root` | Auto-detected (`~/Projects` → `~/Code` → `~/work` → `~/src` → `~/dev`); override with `FORGE_PROJECT_ROOT=...` | `~/.forge-ai/config.json` |
| OS | Detected at install time | macOS · Linux · Windows (Git Bash, less battle-tested) |

The two diagnostic files (`last-run.log`, `last-payload.json`) are the only window into hook behavior — see architecture doc Section 10 for troubleshooting.

---

## 6 — What we collect / what we don't

| ✅ Sent to Forge API | ❌ Never leaves the machine |
|---|---|
| Commit SHA, branch, repo name + URL | Source code |
| File **paths** + line **ranges** (no content) | Prompts / AI responses |
| Agent tool + model (e.g., `claude` / `claude-opus-4-7`) | Full diffs |
| Author name (extracted from git-ai note) | SCM credentials |
| Line counts + diff stats (counts only) | Anything outside the metadata above |

**Tenant isolation:** every row stamped with `team_id`; every query filters on it; the API key resolves to exactly one tenant. Same pattern Forge platform uses for other multi-tenant endpoints.

---

## 7 — Forge API + DB extensions

Concrete additions to the existing platform — kept deliberately small.

### Tenant provisioning *(existing process — not part of this design)*

Whichever process Forge already uses to provision a multi-tenant client yields the two values we need:

- `TEAM_ID` — opaque identifier (UUID) that scopes every metrics row
- `API_KEY` — secret string the developer's machine sends as `X-API-Key`; the Forge platform hashes and resolves it back to one `TEAM_ID`

This document **does not** specify how those are minted, stored, or rotated — that's the responsibility of the platform-level multi-tenancy work happening in parallel. We just consume the result.

### New `Commits` table in Forge DB

One new table holds every ingested commit. Every row is stamped with `team_id`; every read filters on it.

| Column | Type | Notes |
|---|---|---|
| `Id` | UUID (PK) | Server-generated row id |
| `TeamId` | UUID | Resolved from `X-API-Key` on every ingest. Foreign-keys into the platform-level tenancy table. |
| `RepoName` | string | E.g. `gitai-service-b` |
| `RepoUrl` | string · nullable | E.g. `https://github.com/.../gitai-service-b.git` |
| `CommitSha` | string(40) | Full SHA |
| `Branch` | string · nullable | |
| `IsDefaultBranch` | bool | |
| `CommitAuthor` | string · nullable | Extracted server-side from the git-ai note (`prompts.<id>.human_author` / `humans.<id>.human_author`) |
| `Agent` | string · nullable | `claude` / `github-copilot` / `cursor` / `codex` / … |
| `Model` | string · nullable | E.g. `claude-opus-4-7` |
| `AgentLines` | int | Lines authored through an AI agent |
| `HumanLines` | int | Lines authored by a human |
| `OverriddenLines` | int | AI lines later modified by the human |
| `AgentPercentage` | decimal(5,1) | `AgentLines / (AgentLines + HumanLines) × 100` |
| `DiffAdditions` | int | From `git diff --numstat` (enriched client-side) |
| `DiffDeletions` | int | From `git diff --numstat` |
| `CommittedAt` | timestamp · nullable | Actual commit time (enriched). Used for time-series. |
| `IngestedAt` | timestamp | Server-set on insert |
| `RawNote` | text | Verbatim git-ai note — kept for replay if the parser changes |

**Idempotency key:** `UNIQUE(TeamId, RepoName, CommitSha)`. Re-submitting the same commit (retries, daemon re-emits, post-rebase rewrites) upserts safely — no duplicates.

### New Forge API endpoints

| Method | Path | Purpose | Returns |
|---|---|---|---|
| `POST` | `/api/coding-metrics/ingest` | Hook calls this on every commit | `IngestResponse` (commit id + computed attribution) |
| `GET` | `/api/coding-metrics/summary?period=30d` | Top-line tenant numbers | `{total_commits, ai_commits, ai_percentage, total_ai_lines, total_human_lines}` |
| `GET` | `/api/coding-metrics/by-agent?period=30d` | Per agent / model breakdown | `[{agent, commits, ai_lines, avg_percentage}]` |
| `GET` | `/api/coding-metrics/by-developer?period=30d` | Per author leaderboard | `[{author, commits, ai_percentage, ai_lines}]` |
| `GET` | `/api/coding-metrics/by-repo?period=30d` | Per repo breakdown | `[{repo_name, commits, ai_percentage, ai_lines}]` |

All endpoints require `X-API-Key` (resolves to a single `TeamId`); period accepts `7d` / `30d` / `90d` / custom range. Reads use `CommittedAt` (actual commit time), not `IngestedAt`, so time-series charts reflect when work happened.

---

## 8 — Onboarding pipeline (internal team or external tenant)

The metrics-collection logic ships **inside** the WoD AI SSD package — no external setup URL, no separate installer to host. The developer's normal package install (or update) already includes git-ai install + the hook script + the config writer. Onboarding is purely about **provisioning a tenant key and handing it to developers**.

```mermaid
sequenceDiagram
    autonumber
    actor Admin as Forge AI Admin
    actor Lead as Tenant team lead
    actor Devs as Tenant developers<br/>(WoD AI SSD installed)
    participant Forge as Forge API + DB
    participant UI as Forge dashboard

    Admin->>Forge: Provision tenant<br/>(existing platform process)<br/>→ TEAM_ID + API_KEY
    Admin->>Lead: Send TEAM_ID + API_KEY<br/>(secure channel)
    Lead->>Devs: Distribute TEAM_ID + API_KEY<br/>(Slack / email / wiki)
    Devs->>Devs: Configure WoD AI SSD with TEAM_ID + API_KEY<br/>(env var / config / CLI flag) — ~30s, idempotent
    Devs->>Devs: Package activates metrics hook
    Note over Devs: writes ~/.forge-ai/config.json<br/>registers post_notes_updated<br/>restarts git-ai daemon
    loop every AI commit
        Devs->>Forge: POST /api/coding-metrics/ingest<br/>(metadata only)
        Forge-->>Devs: 200 OK
    end
    Lead->>UI: Open "Coding metrics" view
    UI->>Forge: Query metrics (tenant-scoped by TEAM_ID)
    Forge-->>UI: Per-team / dev / repo / agent rollups
```

**Total time admin → first data**: ~10 minutes (admin work) + ~30 seconds per developer (just configuring the key — package is already installed) + the next AI commit. Existing tenants already running the WoD AI SSD package only need to apply their tenant key — no re-install.

---

## 9 — Worked example: what one commit looks like in the database

A real ingested commit from a multi-repo Claude Code session 

### What the developer did

In one Claude Code session, asked Claude to add fields to `test.json` in three sibling repos (`gitai-workspace`, `gitai-service-a`, `gitai-service-b`). The developer also typed some lines manually. `service-b` ended up with the most mixed file map.

### What landed in `Commits`

| Column | Value |
|---|---|
| `Id` | `738DE4DC-759A-4559-9058-B8F0D1AA7919` |
| `TeamId` | `AD803EFC-A21B-4556-B5B4-3D1B1DEC0C12` *(Platform Team)* |
| `RepoName` | `gitai-service-b` |
| `RepoUrl` | `https://github.com/ShamsiievDmytro/gitai-service-b.git` |
| `CommitSha` | `ae20f6f29e22293875cbfa6e6252db59360d960a` |
| `Branch` | `main` |
| `IsDefaultBranch` | `1` |
| `CommitAuthor` | `Dmytro Shamsiiev`|
| `Agent` | `claude` |
| `Model` | `claude-opus-4-7` |
| `AgentLines` | `2` |
| `HumanLines` | `10` |
| `OverriddenLines` | `1` *(human modified an AI line)* |
| `AgentPercentage` | `16.7` |
| `DiffAdditions` | `12` *(enriched: `git diff --numstat`)* |
| `DiffDeletions` | `1` *(enriched)* |
| `CommittedAt` | `2026-04-29 08:55:05` *(enriched: `git log -1 --format=%aI`)* |
| `IngestedAt` | `2026-04-29 08:55:08.047` *(server)* |
| `RawNote` | *(see below — full git-ai note stored verbatim for replay)* |

### `RawNote` — verbatim git-ai output stored for forward-compatibility

```
test.json
  h_dca485b1adf836 3-4,7-14
  d4a7828bfdafe456 5-6
---
{
  "schema_version": "authorship/3.0.0",
  "git_ai_version": "1.3.4",
  "base_commit_sha": "ae20f6f29e22293875cbfa6e6252db59360d960a",
  "prompts": {
    "d4a7828bfdafe456": {
      "agent_id": { "tool": "claude", "model": "claude-opus-4-7" },
      "human_author": "Dmytro Shamsiiev",
      "total_additions": 3, "total_deletions": 1,
      "accepted_lines": 2, "overriden_lines": 1
    }
  },
  "humans": { "h_dca485b1adf836": { "author": "Dmytro Shamsiiev" } }
}
```

### How the columns are derived

| Derived column | Source | Computation |
|---|---|---|
| `AgentLines = 2` | file map | sum of ranges for prompt id `d4a7828b…` → lines 5-6 |
| `HumanLines = 10` | file map | sum of ranges for human id `h_dca485b1…` → lines 3-4 (2) + 7-14 (8) = 10 |
| `AgentPercentage = 16.7` | computed | 2 / (2 + 10) × 100 |
| `OverriddenLines = 1` | prompts JSON | `prompts.d4a7828b….overriden_lines` *(note the git-ai 1.3.4 spelling)* |
| `Agent / Model` | prompts JSON | joined from contributing prompt ids: `claude` / `claude-opus-4-7` |
| `CommitAuthor` | prompts JSON | `prompts.<id>.human_author` (top-level payload has no author field — extracted server-side) |
| `DiffAdditions / Deletions` | enrichment | `git diff --numstat ae20f6f2^!` — 12 added, 1 deleted |
| `CommittedAt` | enrichment | `git log -1 --format=%aI ae20f6f2` — ISO 8601 with timezone |

This is exactly what the Forge dashboard's "Coding metrics" view will read for every per-tenant / per-developer / per-repo / per-agent rollup.

---

## 10 — Status & validation

**What's verified working today** (with git-ai 1.3.4):

| Agent | Status |
|---|---|
| Claude Code (CLI) | ✅ Working — fully attributed, multi-repo verified |
| Claude Code (VS Code / JetBrains extension) | ✅ Working |
| GitHub Copilot (VS Code extension) | ✅ Working |
| Cursor | ⏳ Blocked on git-ai upstream — see [issue #1204](https://github.com/git-ai-project/git-ai/issues/1204) |
| Codex | ⏳ Blocked on git-ai upstream — see [issue #1204](https://github.com/git-ai-project/git-ai/issues/1204) |


**Production readiness:** deployable for Claude (CLI + extension) and Copilot tenants as soon as Forge API multi-tenancy lands. Cursor and Codex coverage activates automatically the moment git-ai upstream ships the fix in [#1204](https://github.com/git-ai-project/git-ai/issues/1204) — no changes needed on our side. Other open items are tracked in Section 9 of the architecture doc (squash/rebase reconciliation, multi-agent conflict).

---

## 11 — Honest limitations (pre-empting the Q&A)


| # | Limitation | Why it matters | Mitigation in v1 |
|---|---|---|---|
| L1 | **Local-only commits are included** | The hook fires on every commit, including branches that may never be pushed. Dashboard can over-count vs. "code that shipped". | Document expectation as "AI in drafts and final code combined." A future v2 could reconcile against SCM. |
| L2 | **Multi-agent conflict** | When two agents (e.g., Claude + Copilot) are active simultaneously, the first agent to checkpoint claims file changes — wrong tool may be credited. | Rare in practice; visible in `RawNote` for forensic re-derivation. |
| L3 | **100 % AI attribution can over-count when work happens entirely inside an integrated AI editor** | git-ai attributes by *who saved the bytes through an editor hook*, not by who originated the idea. Lines you guided keystroke-by-keystroke through Claude Code still tag as AI. | Upstream attribution-model trade-off, not a server bug. Calibrate expectations: the metric is "code authored through AI tooling", not "lines invented by AI". |
| L4 | **Cursor + Codex coverage pending** | Currently those agents produce empty `prompts: {}` notes. | Tracked at git-ai [#1204](https://github.com/git-ai-project/git-ai/issues/1204); coverage activates automatically when the upstream fix lands. |

---

## 📎 Prototype attachment

A working prototype implementation is attached to this page as **`prototype.zip`** — it contains the .NET ingest API, the MS SQL schema, the `enrich-and-post.sh` hook script, and the `setup.sh.tmpl` developer-side bootstrap. End-to-end validated locally and across multiple repos before this doc was written; treat it as a reference implementation rather than the v1 production codebase. See the deep-dive doc ([`ai-code-metrics-architecture.md`](./ai-code-metrics-architecture.md)) Section 10 for how to run it.