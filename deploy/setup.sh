#!/usr/bin/env bash
# =============================================================================
# Daleel VPS bootstrap — Ubuntu 22.04 / 24.04 (tested on Hetzner CX23).
#
# Run once, as root, on a fresh server:
#   ssh root@your-server 'bash -s' < deploy/setup.sh
# or copy this repo's deploy/ dir to the box and run:
#   sudo ./setup.sh
#
# Installs Docker + Compose, lays out /opt/daleel, installs a systemd unit,
# configures UFW + log rotation. Everything runs as root — no service user.
# =============================================================================
set -euo pipefail

APP_DIR="/opt/daleel"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

log() { printf '\033[1;32m==>\033[0m %s\n' "$*"; }
err() { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; }

if [ "$(id -u)" -ne 0 ]; then
  err "This script must be run as root (use sudo)."
  exit 1
fi

# ---------------------------------------------------------------------------
# 1. Base packages
# ---------------------------------------------------------------------------
log "Updating apt and installing prerequisites..."
export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y ca-certificates curl gnupg ufw

# ---------------------------------------------------------------------------
# 2. Docker Engine + Compose plugin (official repo)
# ---------------------------------------------------------------------------
if ! command -v docker >/dev/null 2>&1; then
  log "Installing Docker Engine + Compose plugin..."
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
    | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  chmod a+r /etc/apt/keyrings/docker.gpg

  . /etc/os-release
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] \
https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
    > /etc/apt/sources.list.d/docker.list

  apt-get update -y
  apt-get install -y docker-ce docker-ce-cli containerd.io \
    docker-buildx-plugin docker-compose-plugin
  systemctl enable --now docker
else
  log "Docker already installed — skipping."
fi

# ---------------------------------------------------------------------------
# 3. /opt/daleel layout + deploy artifacts (owned by root)
# ---------------------------------------------------------------------------
log "Setting up $APP_DIR ..."
mkdir -p "$APP_DIR"

# Copy compose + Caddyfile + deploy script from this repo's deploy/ dir.
for f in docker-compose.yml Caddyfile deploy.sh; do
  if [ -f "$SCRIPT_DIR/$f" ]; then
    install -m 0644 "$SCRIPT_DIR/$f" "$APP_DIR/$f"
  fi
done
chmod +x "$APP_DIR/deploy.sh" 2>/dev/null || true

# Seed a placeholder .env if not present. NOTE: you do NOT edit this by hand —
# the GitHub Actions deploy workflow (.github/workflows/deploy.yml) rewrites
# $APP_DIR/.env from the repo's GitHub secrets on EVERY deploy. This seed only
# exists so the systemd unit / `docker compose up` has a file to read if the box
# reboots before the first deploy runs.
if [ ! -f "$APP_DIR/.env" ] && [ -f "$SCRIPT_DIR/.env.example" ]; then
  install -m 0600 "$SCRIPT_DIR/.env.example" "$APP_DIR/.env"
  log "Seeded placeholder $APP_DIR/.env — the deploy workflow overwrites it from GitHub secrets."
fi

chmod 0600 "$APP_DIR/.env" 2>/dev/null || true

# ---------------------------------------------------------------------------
# 4. systemd unit — keeps the compose stack up across reboots
# ---------------------------------------------------------------------------
log "Installing systemd unit daleel.service ..."
cat > /etc/systemd/system/daleel.service <<UNIT
[Unit]
Description=Daleel production stack (docker compose)
Requires=docker.service
After=docker.service network-online.target
Wants=network-online.target

[Service]
Type=oneshot
RemainAfterExit=true
WorkingDirectory=${APP_DIR}
ExecStart=/usr/bin/docker compose up -d --wait
ExecStop=/usr/bin/docker compose down
ExecReload=/usr/bin/docker compose up -d --wait
TimeoutStartSec=300

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable daleel.service

# ---------------------------------------------------------------------------
# 5. UFW firewall — SSH + HTTP + HTTPS only
# ---------------------------------------------------------------------------
log "Configuring UFW firewall (22, 80, 443)..."
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw allow 443/udp           # HTTP/3 (QUIC)
ufw --force enable

# ---------------------------------------------------------------------------
# 6. Log rotation for container json logs (belt-and-suspenders alongside
#    the per-service max-size limits in docker-compose.yml)
# ---------------------------------------------------------------------------
log "Configuring Docker daemon log rotation..."
mkdir -p /etc/docker
if [ ! -f /etc/docker/daemon.json ]; then
  cat > /etc/docker/daemon.json <<'JSON'
{
  "log-driver": "json-file",
  "log-opts": {
    "max-size": "10m",
    "max-file": "3"
  }
}
JSON
  systemctl restart docker
fi

cat > /etc/logrotate.d/daleel <<'ROT'
/var/lib/docker/containers/*/*.log {
  rotate 7
  daily
  compress
  missingok
  delaycompress
  copytruncate
}
ROT

# ---------------------------------------------------------------------------
log "Done."
cat <<NEXT

Next steps:
  1. Do NOT hand-edit ${APP_DIR}/.env — the GitHub Actions deploy workflow writes
     it from the repo's GitHub secrets on every deploy. Just make sure those
     secrets are set (see deploy/create-secrets.sh and deploy/.env.example).
  2. Point DNS: daleel.hamzalabs.dev (A/AAAA) -> this server's IP.
  3. Log in to GHCR if the image is private:
       docker login ghcr.io
  4. Trigger a deploy from GitHub (Actions -> Deploy, or push a v* tag). It will
     write ${APP_DIR}/.env and bring the stack up. To start locally instead:
       systemctl start daleel.service
     or run the deploy script directly:
       ${APP_DIR}/deploy.sh latest
NEXT
