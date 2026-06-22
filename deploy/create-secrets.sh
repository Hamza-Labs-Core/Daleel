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
  CLOUDFLARE_ACCOUNT_ID
  CLOUDFLARE_API_TOKEN
  APIFY_TOKEN
  # Deploy (consumed by .github/workflows/deploy.yml)
  DEPLOY_SSH_HOST
  DEPLOY_SSH_USER
  DEPLOY_SSH_KEY
)

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
