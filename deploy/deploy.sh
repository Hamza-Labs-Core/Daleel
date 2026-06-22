#!/usr/bin/env bash
# =============================================================================
# Daleel deploy — pull a new image, restart the stack, health-check, rollback.
#
# Usage (run from /opt/daleel, as the daleel user):
#   ./deploy.sh [TAG]        # TAG defaults to "latest"
#
# Mechanics:
#   * Records the currently-running image so we can roll back to the exact bits.
#   * Pulls the requested tag and brings the stack up with --wait (blocks until
#     container healthchecks pass).
#   * Curls /health through the running container; on any failure, re-pins the
#     previous image and restarts.
# =============================================================================
set -euo pipefail

REGISTRY_IMAGE="ghcr.io/hamza-labs-core/daleel"
TAG="${1:-${DALEEL_TAG:-latest}}"
NEW_IMAGE="${REGISTRY_IMAGE}:${TAG}"
HEALTH_RETRIES=10
HEALTH_DELAY=6   # seconds between health attempts

cd "$(dirname "${BASH_SOURCE[0]}")"

log()  { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mWARN:\033[0m %s\n' "$*" >&2; }
err()  { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; }

# docker compose v2 (plugin) is required.
compose() { docker compose "$@"; }

health_check() {
  local i
  for i in $(seq 1 "$HEALTH_RETRIES"); do
    if compose exec -T daleel curl --fail --silent http://localhost:8080/health >/dev/null 2>&1; then
      log "Health check passed (attempt $i)."
      return 0
    fi
    warn "Health check attempt $i/$HEALTH_RETRIES failed; retrying in ${HEALTH_DELAY}s..."
    sleep "$HEALTH_DELAY"
  done
  return 1
}

# ---------------------------------------------------------------------------
# 1. Capture the currently-running image for rollback (digest if available).
# ---------------------------------------------------------------------------
PREVIOUS_IMAGE="$(docker inspect --format '{{.Image}}' daleel 2>/dev/null || true)"
if [ -n "$PREVIOUS_IMAGE" ]; then
  log "Current running image: $PREVIOUS_IMAGE"
else
  warn "No running 'daleel' container found — this looks like a first deploy."
fi

# ---------------------------------------------------------------------------
# 2. Pull and roll out the new image.
# ---------------------------------------------------------------------------
log "Deploying $NEW_IMAGE ..."
export DALEEL_IMAGE="$NEW_IMAGE"

docker pull "$NEW_IMAGE"

if compose up -d --wait --remove-orphans && health_check; then
  log "Deploy succeeded: $NEW_IMAGE is live and healthy."
  # Reclaim disk from old image layers (keep it tidy on a small CX23).
  docker image prune -f >/dev/null 2>&1 || true
  exit 0
fi

# ---------------------------------------------------------------------------
# 3. Rollback.
# ---------------------------------------------------------------------------
err "Deploy failed health check."
if [ -z "$PREVIOUS_IMAGE" ]; then
  err "No previous image to roll back to. Leaving stack as-is for inspection."
  compose logs --tail=50 daleel || true
  exit 1
fi

warn "Rolling back to $PREVIOUS_IMAGE ..."
export DALEEL_IMAGE="$PREVIOUS_IMAGE"
if compose up -d --wait --remove-orphans && health_check; then
  err "Rolled back to previous image successfully. The new deploy was aborted."
  exit 1
fi

err "ROLLBACK ALSO FAILED — manual intervention required."
compose logs --tail=50 daleel || true
exit 2
