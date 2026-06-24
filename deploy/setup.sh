#!/usr/bin/env bash
# =============================================================================
# Daleel VPS setup — NO LONGER A MANUAL STEP.
#
# Provisioning is now handled automatically by the deploy workflow
# (.github/workflows/deploy.yml). Its "Bootstrap VPS" step SSHes into the box
# and, idempotently, installs Docker + Compose, creates /opt/daleel, installs
# the daleel.service systemd unit, opens UFW ports (22/80/443), configures log
# rotation, and logs in to GHCR — provisioning a fresh box and skipping fast on
# an already-provisioned one. The deploy is a single self-contained action.
#
# To set up a brand-new VPS: just run the Deploy workflow (Actions -> Deploy, or
# push a `v*` tag) and approve the `production` gate. No SSH bootstrap required.
# =============================================================================
cat <<'MSG'
deploy/setup.sh is no longer needed.

VPS setup is handled automatically by the deploy workflow's "Bootstrap VPS"
step (.github/workflows/deploy.yml). To provision and deploy in one action:

  * push a `v*` tag, or
  * run the "Deploy" workflow from the GitHub Actions tab,

then approve the `production` environment gate. The workflow installs Docker,
creates /opt/daleel, opens the firewall, sets up systemd + log rotation, logs
in to GHCR, and rolls out the image — all idempotently.
MSG
