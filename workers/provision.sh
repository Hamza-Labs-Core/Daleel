#!/usr/bin/env bash
# Idempotent Cloudflare resource provisioning for one worker + one environment:
#
#   ./workers/provision.sh <worker-dir-name> <prod|qa>
#
# CHECK-THEN-CREATE for everything a worker binds: queues (+ DLQs + the poll queue's HTTP pull
# consumer) for scrape-worker, the KV namespace for search-worker (resolved by TITLE, created when
# missing, and its id injected into wrangler.jsonc's placeholder). Safe to run on every deploy —
# existing resources are left untouched. Each environment's deploy workflow runs this before its
# deploy (deploy-workers.yml -> prod, deploy-workers-qa.yml -> qa); operators run the exact same script for a manual/first-time setup.
#
# Auth: CLOUDFLARE_API_TOKEN + CLOUDFLARE_ACCOUNT_ID in the environment (wrangler reads both).
# Run from the repo root or the worker directory — the script cd's itself.
set -euo pipefail

WORKER="${1:?usage: provision.sh <worker> <prod|qa>}"
ENVIRONMENT="${2:?usage: provision.sh <worker> <prod|qa>}"

case "$ENVIRONMENT" in
  prod) PREFIX="daleel" ;;
  qa)   PREFIX="daleel-qa" ;;
  *) echo "::error::environment must be 'prod' or 'qa', got '$ENVIRONMENT'" >&2; exit 1 ;;
esac

cd "$(dirname "$0")/$WORKER"

# ── helpers ─────────────────────────────────────────────────────────────────────

ensure_queue() {
  local name="$1"
  if npx wrangler queues info "$name" >/dev/null 2>&1; then
    echo "queue exists: $name"
  else
    echo "creating queue: $name"
    npx wrangler queues create "$name"
  fi
}

# The VPS drain retries a message with backoff until the job's deadline (~30 min); the default
# retry budget (3) would delete messages it is still legitimately retrying — hence 30.
ensure_pull_consumer() {
  local queue="$1" dlq="$2"
  local out
  if out=$(npx wrangler queues consumer http add "$queue" \
      --message-retries 30 --dead-letter-queue "$dlq" 2>&1); then
    echo "pull consumer attached: $queue (retries=30, dlq=$dlq)"
  elif grep -qi "consumer" <<<"$out"; then
    # Already has a consumer — the idempotent no-op case.
    echo "pull consumer already attached: $queue"
  else
    echo "::error::attaching pull consumer to $queue failed:" >&2
    echo "$out" >&2
    exit 1
  fi
}

# Resolves a KV namespace id by TITLE (creating the namespace when missing) and injects it into
# wrangler.jsonc at the given placeholder token. A config whose placeholder was already replaced
# (operator committed a real id) is left untouched.
ensure_kv_namespace() {
  local title="$1" placeholder="$2"
  if ! grep -q "$placeholder" wrangler.jsonc; then
    echo "kv id already set in wrangler.jsonc (no $placeholder placeholder) — skipping $title"
    return
  fi

  local id
  id=$(npx wrangler kv namespace list 2>/dev/null \
        | node -e '
            let s = "";
            process.stdin.on("data", d => (s += d));
            process.stdin.on("end", () => {
              const list = JSON.parse(s);
              const hit = list.find(n => n.title === process.argv[1]);
              if (hit) process.stdout.write(hit.id);
            });
          ' "$title")

  if [ -z "$id" ]; then
    echo "creating kv namespace: $title"
    npx wrangler kv namespace create "$title" >/dev/null
    id=$(npx wrangler kv namespace list 2>/dev/null \
          | node -e '
              let s = "";
              process.stdin.on("data", d => (s += d));
              process.stdin.on("end", () => {
                const list = JSON.parse(s);
                const hit = list.find(n => n.title === process.argv[1]);
                if (hit) process.stdout.write(hit.id);
              });
            ' "$title")
  fi

  if [ -z "$id" ]; then
    echo "::error::could not resolve kv namespace id for $title" >&2
    exit 1
  fi

  echo "kv namespace $title -> $id (injecting into wrangler.jsonc)"
  # Plain token substitution — placeholders are distinctive strings, safe under sed.
  sed -i.bak "s/$placeholder/$id/" wrangler.jsonc && rm -f wrangler.jsonc.bak
}

# ── per-worker resources ────────────────────────────────────────────────────────

case "$WORKER" in
  scrape-worker)
    ensure_queue "$PREFIX-scrape-work"
    ensure_queue "$PREFIX-scrape-dlq"
    ensure_queue "$PREFIX-poll-work"
    ensure_queue "$PREFIX-poll-dlq"
    ensure_pull_consumer "$PREFIX-poll-work" "$PREFIX-poll-dlq"
    ;;
  search-worker)
    if [ "$ENVIRONMENT" = "prod" ]; then
      ensure_kv_namespace "daleel-search-cache" "PLACEHOLDER_SEARCH_CACHE_KV_ID_PROD"
    else
      ensure_kv_namespace "daleel-qa-search-cache" "PLACEHOLDER_SEARCH_CACHE_KV_ID_QA"
    fi
    ;;
  classify-worker|extract-worker|filter-worker|log-viewer)
    # Workers-AI / R2-read-only workers: the AI binding is account-level and buckets are managed
    # by the app's own provisioning — nothing to create here.
    echo "no provisionable resources for $WORKER"
    ;;
  *)
    echo "::error::unknown worker '$WORKER'" >&2
    exit 1
    ;;
esac

echo "provisioning complete: $WORKER ($ENVIRONMENT)"
