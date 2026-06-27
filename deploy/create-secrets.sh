#!/usr/bin/env bash
# =============================================================================
# Create placeholder GitHub repository secrets for Daleel.
#
# Every secret is seeded with the value "CHANGE_ME" so the workflows resolve;
# fill in the real values afterwards via the GitHub UI
# (Settings -> Secrets and variables -> Actions) or by re-running:
#   gh secret set NAME --body "real-value" --repo Hamza-Labs-Core/Daleel
#
# Requires the GitHub CLI authenticated with admin access to the repo:
#   gh auth login
# =============================================================================
set -euo pipefail

REPO="${REPO:-Hamza-Labs-Core/Daleel}"
PLACEHOLDER="CHANGE_ME"

SECRETS=(
  # LLM + data providers (consumed by the app at runtime)
  OPENROUTER_API_KEY
  SERPAPI_KEY
  CONTEXT_DEV_API_KEY
  GOOGLE_PLACES_API_KEY
  APIFY_TOKEN
  # Deploy (consumed by .github/workflows/deploy.yml)
  DEPLOY_SSH_HOST
  DEPLOY_SSH_USER
  DEPLOY_SSH_KEY
)

# NOTE: CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN are intentionally NOT created
# here — they are provided at the GitHub org level, so they don't need a per-repo secret.
# The app still reads them at runtime via /opt/daleel/.env (see deploy/.env.example).
#
# NOTE: the R2 object-storage secrets (R2_ACCESS_KEY, R2_SECRET_KEY) are also intentionally
# NOT seeded. They are OPTIONAL — when unset the app falls back to file logging + hot-linked
# images — and seeding them with the CHANGE_ME placeholder would make the app think R2 is
# configured and try to connect with bogus credentials. Set them by hand only if you want R2:
# gh secret set R2_ACCESS_KEY --body '<real>' --repo "$REPO" (and likewise for the secret key).
# Bucket NAMES default to daleel-{logs,images,specs,data} — just create those buckets; override
# via R2_BUCKET_{LOGS,IMAGES,SPECS,DATA} only if named differently. To serve images from R2, also
# set R2_PUBLIC_URL_IMAGES. The R2 endpoint is derived from CLOUDFLARE_ACCOUNT_ID.
#
# NOTE: PostgreSQL is REQUIRED — the whole app runs on it (main `daleel` DB, the `daleel_events`
# event store, and Elsa's workflow store). POSTGRES_PASSWORD (for the bundled postgres service) and
# POSTGRES_CONNECTION_STRING must be set as GitHub secrets; the deploy workflow renders them into
# /opt/daleel/.env (see deploy/.env.example). The app fails fast at startup if no Postgres connection
# is configured, so these are not optional.

if ! command -v gh >/dev/null 2>&1; then
  echo "ERROR: GitHub CLI (gh) is not installed. See https://cli.github.com/" >&2
  exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
  echo "ERROR: gh is not authenticated. Run: gh auth login" >&2
  exit 1
fi

echo "Creating ${#SECRETS[@]} placeholder secrets on $REPO ..."
for name in "${SECRETS[@]}"; do
  gh secret set "$name" --body "$PLACEHOLDER" --repo "$REPO"
  echo "  set $name = $PLACEHOLDER"
done

echo "Done. Replace each placeholder with the real value before deploying."
