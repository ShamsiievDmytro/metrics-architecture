# Forge AI Metrics — Developer Onboarding

**Time required:** ~2 minutes.
**What you'll get:** every commit you make through an AI coding agent is automatically attributed (AI %, lines, agent/model) and shows up in the team dashboard.

If you hit anything this guide doesn't cover, the deep-dive reference is `2026-04-28-forge-ai-metrics-architecture.md` in the repo root — Section 10 is the troubleshooting playbook.

---

## What your team lead will give you

1. The setup URL — looks like `https://forge-ai-metrics.<your-company>.com/setup/<TEAM_ID>/<API_KEY>`
2. (Optionally) an override for `FORGE_PROJECT_ROOT` if your repos don't live under `~/Projects`

That's it. The setup command bundles your team id and API key in the URL — no manual config.

---

## Prerequisites

You only need three things; the setup script installs the rest:

- **macOS or Linux** (Windows via Git Bash works but is less battle-tested)
- **`curl`, `git`** already on `PATH`
- **A coding agent integrated with git-ai** — Claude Code, Cursor, GitHub Copilot, Codex, Continue, Amp, Windsurf, Firebender, JetBrains plugin. (Plain `git commit` from the shell with no AI involvement produces no attribution data — that's by design.)

The setup script installs `jq` and `git-ai` automatically if they're missing.

---

## Setup (one command)

```bash
curl -s https://forge-ai-metrics.<your-company>.com/setup/<TEAM_ID>/<API_KEY> | bash
```

If your repos live somewhere other than `~/Projects`:

```bash
FORGE_PROJECT_ROOT="$HOME/work" \
  curl -s https://forge-ai-metrics.<your-company>.com/setup/<TEAM_ID>/<API_KEY> | bash
```

You should see output ending with:

```
[forge-ai] hook registered: ["/Users/<you>/.forge-ai/enrich-and-post.sh"]
[forge-ai] async_mode:       true
[forge-ai] prompt_storage:   "local"
[forge-ai] config: /Users/<you>/.forge-ai/config.json
[forge-ai] setup complete.
```

If any of those values come back as `MISSING` / `unknown` / `false`, see Troubleshooting → "Setup completed but values look wrong."

---

## What setup actually did

It's all per-developer / global — it's not tied to any individual repo.

| File / setting | Purpose |
|---|---|
| `~/.git-ai/` (binary, daemon, db) | Installed/upgraded by `git-ai` itself |
| `~/.git-ai/config.json` | Hook + feature flags so `post_notes_updated` fires |
| `~/.forge-ai/config.json` | Your `api_url`, `api_key`, `team_id`, `project_root` |
| `~/.forge-ai/enrich-and-post.sh` | The hook script: enriches each note with diff stats and POSTs to the ingest API |

**One installation covers every repo on your machine.** No per-repo `.git/hooks/` symlinks, no `core.hooksPath`. Just commit normally; if your AI agent participated in the changes, the data flows automatically.

---

## Verifying it works (smoke test)

Pick any repo under your `project_root`, have your AI agent make a small change, then commit:

```bash
# Clear hook diagnostic logs so you only see this run
: > ~/.forge-ai/last-run.log
rm -f ~/.forge-ai/last-payload.json /tmp/forge-ai-resp.json

# (Have your AI agent edit a file, then)
git commit -am "smoke test: forge-ai onboarding"

# Within ~3 seconds, this should show success:
cat ~/.forge-ai/last-run.log
```

Expected output:

```
=== 2026-04-29T12:34:56Z hook fired (pid=12345) ===
[forge-ai] events=1
[forge-ai] ok http=200 commit=<your-sha>
```

If you see that, your commit is in the database and on the dashboard within seconds.

---

## Where to look when things don't work

The **only** two files that show what the hook is doing:

| File | What it tells you |
|---|---|
| `~/.forge-ai/last-run.log` | Did the hook fire? Did the POST succeed? What HTTP code came back? |
| `~/.forge-ai/last-payload.json` | The raw JSON the daemon sent — useful when debugging payload-shape issues |

Plus the daemon's own log (rarely needed):

```bash
PID=$(jq -r '.pid' ~/.git-ai/internal/daemon/daemon.pid.json)
tail -f ~/.git-ai/internal/daemon/logs/${PID}.log
```

---

## Common issues

### "I commit but nothing shows up in `last-run.log`"

The hook isn't firing. Walk through these in order:

```bash
# 1. Daemon healthy?
git-ai bg status                       # should print { "ok": true, ... }

# 2. Hook + flags set?
git-ai config git_ai_hooks.post_notes_updated   # should list your script
git-ai config feature_flags.async_mode          # → true

# 3. Did your commit have AI activity?
#    Plain `git commit` with zero AI involvement produces no note → no event.
#    If your AI agent really did edit files, check git-ai sees a note:
git-ai log -1
```

If the daemon is healthy and flags are set but commits still don't fire, you may have a stale daemon — see "Zombie daemon" below.

### "Hook fires (`events=1`) but POST fails (`http=4xx` / `http=000`)"

Read the line in `~/.forge-ai/last-run.log` — it includes the response body:

| HTTP code | Meaning | Fix |
|---|---|---|
| `000` | Can't reach API | Check network / VPN, confirm `api_url` in `~/.forge-ai/config.json` |
| `401` | API key wrong | Re-run setup with the correct URL from your team lead |
| `400` | Server rejected payload | Send `~/.forge-ai/last-payload.json` to your team lead |
| `5xx` | Server bug | Send the SHA + `last-payload.json` to your team lead |

### "Setup completed but values look wrong"

Just re-run the setup command — the script is fully idempotent (`unset` + `set`, no duplicates). Re-running is the documented upgrade path for everything: new git-ai version, updated hook script, changed API URL, anything.

```bash
curl -s https://forge-ai-metrics.<your-company>.com/setup/<TEAM_ID>/<API_KEY> | bash
```

### "Hook config got duplicated by re-running setup" (shouldn't happen)

Sanity-check:

```bash
git-ai config git_ai_hooks.post_notes_updated
```

You should see exactly one path. If you see two or more, clean up manually:

```bash
git-ai config unset git_ai_hooks.post_notes_updated
git-ai config set git_ai_hooks.post_notes_updated ~/.forge-ai/enrich-and-post.sh
git-ai bg restart
```

### "Zombie daemon" — hooks fire intermittently

Symptoms: `git-ai bg status` looks fine, but only some commits trigger `last-run.log` entries; multiple `git-ai` processes in `ps`.

Recovery (the kill-it-with-fire procedure):

```bash
git-ai bg stop
for pid in $(pgrep -f "git-ai (bg|daemon)"); do kill -9 "$pid"; done
rm -f ~/.git-ai/internal/daemon/daemon.lock \
      ~/.git-ai/internal/daemon/control.sock \
      ~/.git-ai/internal/daemon/trace2.sock
git-ai bg start
git-ai bg status
```

Then verify only one daemon is running:

```bash
ps -ef | grep -E "git-ai (bg|daemon)" | grep -v grep | wc -l   # should print 1
```

### "I edited the file myself but it shows as 100% AI"

Expected, not a bug. git-ai attributes each line by *who saved it through an integrated editor hook*, not by who originated the idea. If your AI agent's hook is the one writing the file (e.g., Claude Code's `PostToolUse` saving the buffer), all bytes look AI-authored to git-ai.

To get human attribution, either:
- Edit the file in a non-AI flow that has its own git-ai integration (e.g., direct VS Code save with the git-ai plugin but no active AI session)
- Modify a line the AI previously wrote — that bumps `overridden_lines` and adds an `h_…` range

This is an attribution-model limitation in git-ai, not something the team or the server can fix.

---

## Updating

**Re-run the setup command.** That's the upgrade path for:

- New git-ai versions (the script runs `git-ai upgrade`)
- Updated `enrich-and-post.sh` (the script downloads the latest version from the API)
- Changed API URL or team key (admin will give you a new setup URL)
- Any config drift (the script `set`s known-good values every time)

Commits on `main` of this repo's `scripts/setup.sh.tmpl` describe exactly what each release changes.

---

## What gets sent (and what doesn't)

**Sent to the server:**
- Commit SHA, branch, repo name + URL
- File paths and line ranges (no file content)
- Agent tool + model (e.g., `claude` / `claude-opus-4-7`)
- Author name from your `git config user.name`
- Line counts and diff stats (no actual diff content)

**Never sent:**
- Source code
- Prompts or AI responses
- Full diffs
- SCM credentials

See architecture doc Section 7 for the full security posture.

---

## Uninstall

```bash
git-ai config unset git_ai_hooks.post_notes_updated
git-ai bg restart
rm -rf ~/.forge-ai
```

This stops all telemetry. To remove git-ai itself (rarely needed), follow the upstream uninstall instructions at `https://usegitai.com/docs/cli`.

---

## Getting help

- **First:** check `~/.forge-ai/last-run.log` and `~/.forge-ai/last-payload.json` — most issues are diagnosable from these.
- **Second:** read Section 10 of `2026-04-28-forge-ai-metrics-architecture.md` — has SQL queries, daemon log paths, replay commands.
- **Third:** ping your team lead with: setup URL (redact the API key), output of `git-ai bg status`, contents of `~/.forge-ai/last-run.log`, and the last 50 lines of `~/.git-ai/internal/daemon/logs/<pid>.log`.
