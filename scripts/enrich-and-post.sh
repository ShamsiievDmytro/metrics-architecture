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
