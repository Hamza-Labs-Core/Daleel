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
  # Cloudflare R2 — structured error logging (optional; blank values fall back to
  # file logging at /app/data/logs/, so the deploy still succeeds without them).
  # The endpoint is derived from CLOUDFLARE_ACCOUNT_ID (org-level secret), so no
  # R2_ACCOUNT_ID / R2_ENDPOINT secret is needed here.
  R2_ACCESS_KEY
  R2_SECRET_KEY
  R2_BUCKET_NAME
  # Deploy (consumed by .github/workflows/deploy.yml)
  DEPLOY_SSH_HOST
  DEPLOY_SSH_USER
  DEPLOY_SSH_KEY
)

# NOTE: CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN are intentionally NOT created
# here — they are provided at the GitHub org level, so they don't need a per-repo secret.
# The app still reads them at runtime via /opt/daleel/.env (see deploy/.env.example).

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
