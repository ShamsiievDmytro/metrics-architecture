#!/usr/bin/env bash
# enrich-and-post.sh — git-ai post_notes_updated hook.
# Reads JSON payload from stdin, augments with diff stats and committed_at,
# POSTs to the Forge AI ingest endpoint.
set -euo pipefail

# Capture stderr + diagnostics to a rotating log so failed POSTs are debuggable.
# git-ai's daemon discards hook stderr, and this script intentionally `exit 0`s
# on failure to never block telemetry — without this log, failures are invisible.
LOG="${HOME}/.forge-ai/last-run.log"
mkdir -p "$(dirname "$LOG")"
exec 2> >(tee -a "$LOG" >&2)
echo "=== $(date -u +%FT%TZ) hook fired (pid=$$) ===" >&2

CONFIG="${HOME}/.forge-ai/config.json"
if [[ ! -f "$CONFIG" ]]; then
  echo "[forge-ai] missing $CONFIG — re-run the setup command" >&2
  exit 0
fi

API_URL="$(jq -r '.api_url' "$CONFIG")"
API_KEY="$(jq -r '.api_key' "$CONFIG")"
PROJECT_ROOT="$(jq -r '.project_root' "$CONFIG")"

PAYLOAD="$(cat)"

# git-ai's post_notes_updated stdin can be either a single event object OR an
# array of events (one per commit when notes are written in a batch). Normalize
# to an array so the same code handles both.
EVENTS="$(jq -c 'if type=="array" then . else [.] end' <<<"$PAYLOAD")"
echo "[forge-ai] events=$(jq 'length' <<<"$EVENTS")" >&2

post_one() {
  local event="$1"
  local repo_url commit_sha repo_dir="" diff_add="" diff_del="" committed_at=""
  repo_url="$(jq -r '.repo_url // empty' <<<"$event")"
  commit_sha="$(jq -r '.commit_sha // empty' <<<"$event")"

  if [[ -n "$repo_url" && -d "$PROJECT_ROOT" ]]; then
    while IFS= read -r -d '' git_dir; do
      local candidate remote
      candidate="$(dirname "$git_dir")"
      remote="$(git -C "$candidate" config --get remote.origin.url 2>/dev/null || true)"
      if [[ "$remote" == "$repo_url" ]]; then repo_dir="$candidate"; break; fi
    done < <(find "$PROJECT_ROOT" -maxdepth 6 -type d -name '.git' -print0 2>/dev/null)
  fi

  if [[ -n "$repo_dir" && -n "$commit_sha" ]]; then
    local numstat
    numstat="$(git -C "$repo_dir" diff --numstat "${commit_sha}^!" 2>/dev/null || true)"
    if [[ -n "$numstat" ]]; then
      diff_add="$(awk '{a+=$1} END {print a+0}' <<<"$numstat")"
      diff_del="$(awk '{d+=$2} END {print d+0}' <<<"$numstat")"
    fi
    committed_at="$(git -C "$repo_dir" log -1 --format=%aI "$commit_sha" 2>/dev/null || true)"
  fi

  local enriched
  enriched="$(jq \
    --arg add "${diff_add}" \
    --arg del "${diff_del}" \
    --arg ts  "${committed_at}" \
    '
      . as $p
      | $p
      + ( if $add != "" then {diff_additions: ($add|tonumber)} else {} end )
      + ( if $del != "" then {diff_deletions: ($del|tonumber)} else {} end )
      + ( if $ts  != "" then {committed_at: $ts} else {} end )
    ' <<<"$event")"

  local attempt http_code
  for attempt in 1 2 3; do
    http_code="$(curl -sS -o /tmp/forge-ai-resp.json -w '%{http_code}' \
         -X POST "$API_URL/api/ingest" \
         -H "Content-Type: application/json" \
         -H "X-API-Key: $API_KEY" \
         --data "$enriched" 2>>"$LOG" || true)"
    if [[ "$http_code" == 2* ]]; then
      echo "[forge-ai] ok http=$http_code commit=${commit_sha}" >&2
      return 0
    fi
    echo "[forge-ai] attempt=$attempt http=$http_code commit=${commit_sha} body=$(head -c 500 /tmp/forge-ai-resp.json 2>/dev/null)" >&2
    sleep 2
  done
  echo "[forge-ai] failed after 3 attempts (commit ${commit_sha})" >&2
  return 1
}

len="$(jq 'length' <<<"$EVENTS")"
for ((i=0; i<len; i++)); do
  evt="$(jq -c ".[$i]" <<<"$EVENTS")"
  post_one "$evt" || true
done
exit 0  # never block git-ai on telemetry failure
