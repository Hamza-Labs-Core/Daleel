#!/usr/bin/env bash
# =============================================================================
# Daleel VPS bootstrap — Ubuntu 22.04 / 24.04 (tested on Hetzner CX23).
#
# Run once, as root, on a fresh server:
#   ssh root@your-server 'bash -s' < deploy/setup.sh
# or copy this repo's deploy/ dir to the box and run:
#   sudo ./setup.sh
#
# Installs Docker + Compose, creates the daleel service user, lays out
# /opt/daleel, installs a systemd unit, configures UFW + log rotation.
# =============================================================================
set -euo pipefail

APP_DIR="/opt/daleel"
APP_USER="daleel"
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
# 3. Service user
# ---------------------------------------------------------------------------
if ! id "$APP_USER" >/dev/null 2>&1; then
  log "Creating service user '$APP_USER'..."
  useradd --system --create-home --shell /usr/sbin/nologin "$APP_USER"
fi
# Allow the daleel user to talk to the Docker daemon.
usermod -aG docker "$APP_USER"

# ---------------------------------------------------------------------------
# 4. /opt/daleel layout + deploy artifacts
# ---------------------------------------------------------------------------
log "Setting up $APP_DIR ..."
mkdir -p "$APP_DIR"

# Copy compose + deploy script from this repo's deploy/ dir.
# (No reverse proxy — Cloudflare terminates TLS; the app serves HTTP on :80.)
for f in docker-compose.yml deploy.sh; do
  if [ -f "$SCRIPT_DIR/$f" ]; then
    install -m 0644 "$SCRIPT_DIR/$f" "$APP_DIR/$f"
  fi
done
chmod +x "$APP_DIR/deploy.sh" 2>/dev/null || true

# Seed .env from the example if not present (operator fills in real secrets).
if [ ! -f "$APP_DIR/.env" ] && [ -f "$SCRIPT_DIR/.env.example" ]; then
  install -m 0600 "$SCRIPT_DIR/.env.example" "$APP_DIR/.env"
  log "Seeded $APP_DIR/.env from .env.example — EDIT IT and fill in secrets."
fi

chown -R "$APP_USER:$APP_USER" "$APP_DIR"
chmod 0600 "$APP_DIR/.env" 2>/dev/null || true

# ---------------------------------------------------------------------------
# 5. systemd unit — keeps the compose stack up across reboots
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
User=${APP_USER}
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
# 6. UFW firewall — SSH + HTTP only (Cloudflare terminates TLS, no 443 origin)
# ---------------------------------------------------------------------------
log "Configuring UFW firewall (22, 80)..."
ufw allow 22/tcp
ufw allow 80/tcp
ufw --force enable

# ---------------------------------------------------------------------------
# 7. Log rotation for container json logs (belt-and-suspenders alongside
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
  1. Edit ${APP_DIR}/.env and fill in all secrets (see .env.example).
  2. In Cloudflare, add a proxied (orange-cloud) DNS record for your hostname
     pointing at this server's IP. Cloudflare terminates TLS and proxies to the
     origin over HTTP on port 80. No certs are needed on this box.
  3. Log in to GHCR if the image is private:
       sudo -u ${APP_USER} docker login ghcr.io
  4. Start the stack:
       sudo systemctl start daleel.service
     or run the deploy script directly:
       sudo -u ${APP_USER} ${APP_DIR}/deploy.sh latest
NEXT
