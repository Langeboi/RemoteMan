#!/bin/sh
# RemoteMan gateway relay: one-command install on a Linux box (a VM, a VPS, a NAS with Docker).
#
#   curl -fsSL https://raw.githubusercontent.com/langeboi/remoteman/test/all-features/deploy/gateway/install-relay.sh | sudo sh
#
# It asks two questions (the relay's public name, and whether it gets its own certificate or sits behind your
# proxy), installs Docker if it's missing, writes /opt/remoteman-gateway/compose.yaml, starts the relay and prints
# the one-time link that creates the admin console's first admin. Everything else happens in the console.
#
# No questions (for scripts): DOMAIN=remoteman.example.com MODE=caddy|proxy PORT=47200 sh install-relay.sh
set -eu

DIR=${DIR:-/opt/remoteman-gateway}
IMAGE=${IMAGE:-ghcr.io/langeboi/remoteman-gateway:test}

say() { printf '%s\n' "$*"; }
ask() {  # ask "question" default -> answer (reads the terminal even under curl | sh)
  if [ -r /dev/tty ]; then
    printf '%s [%s]: ' "$1" "$2" > /dev/tty
    read -r a < /dev/tty || a=""
  else a=""; fi
  [ -n "$a" ] && printf '%s' "$a" || printf '%s' "$2"
}

if [ "$(id -u)" != 0 ]; then say "Run it as root (or with sudo)."; exit 1; fi

if ! command -v docker >/dev/null 2>&1; then
  say "Docker isn't installed; installing it with get.docker.com..."
  curl -fsSL https://get.docker.com | sh
fi
if ! docker compose version >/dev/null 2>&1; then say "This needs Docker Compose v2 (docker compose). Update Docker and run again."; exit 1; fi

say ""
say "RemoteMan gateway relay"
say "-----------------------"
DOMAIN=${DOMAIN:-$(ask "Public name RemoteMan and connectors will use (DNS must point here or at your proxy)" "remoteman.example.com")}
say ""
say "  caddy  This box gets its own Let's Encrypt certificate. Needs ports 443 (and 80) forwarded to it."
say "  proxy  You already run a reverse proxy (Nginx Proxy Manager, Traefik, Caddy...) that forwards to this box."
MODE=${MODE:-$(ask "caddy or proxy" "caddy")}
PORT=${PORT:-47200}
if [ "$MODE" = proxy ]; then PORT=$(ask "Port the relay listens on for your proxy" "$PORT"); fi

case "$DOMAIN" in http://*|https://*) URL=$DOMAIN; HOST=$(printf '%s' "$DOMAIN" | sed -E 's#^https?://##; s#/.*##') ;; *) URL="https://$DOMAIN"; HOST=$DOMAIN ;; esac

mkdir -p "$DIR"
if [ -f "$DIR/compose.yaml" ]; then cp "$DIR/compose.yaml" "$DIR/compose.yaml.bak"; say "Kept the old file as $DIR/compose.yaml.bak"; fi

if [ "$MODE" = proxy ]; then
cat > "$DIR/compose.yaml" <<EOF
# RemoteMan gateway relay behind your own proxy. Point the proxy at http://<this box>:$PORT with WebSocket support.
services:
  relay:
    image: $IMAGE
    container_name: remoteman-relay
    command: relay
    restart: unless-stopped
    environment:
      PUBLIC_URL: $URL
      PORT: $PORT
      # ADMIN_ALLOW: 192.168.0.0/16   # only these addresses may open the admin console
    ports:
      - "$PORT:$PORT"
    volumes:
      - relay-data:/data
volumes:
  relay-data:
EOF
else
cat > "$DIR/compose.yaml" <<EOF
# RemoteMan gateway relay with Caddy in front (Let's Encrypt certificate for $HOST).
services:
  relay:
    image: $IMAGE
    container_name: remoteman-relay
    command: relay
    restart: unless-stopped
    environment:
      PUBLIC_URL: $URL
      # ADMIN_ALLOW: 192.168.0.0/16   # only these addresses may open the admin console
    volumes:
      - relay-data:/data
  caddy:
    image: caddy:2
    container_name: remoteman-relay-caddy
    restart: unless-stopped
    command: caddy reverse-proxy --from $HOST --to relay:8080
    ports:
      - "443:443"
      - "80:80"
    volumes:
      - caddy-data:/data
volumes:
  relay-data:
  caddy-data:
EOF
fi

cd "$DIR"
docker compose pull -q
docker compose up -d

say ""
say "Waiting for the relay to start..."
i=0
LINK=""
while [ $i -lt 30 ]; do
  LINK=$(docker logs remoteman-relay 2>&1 | grep -o 'https\?://[^ ]*/admin/#setup=[A-Za-z0-9_-]*' | tail -1 || true)
  if [ -n "$LINK" ] || docker logs remoteman-relay 2>&1 | grep -q 'Admin console: http'; then break; fi
  i=$((i + 1)); sleep 1
done

say ""
if [ -n "$LINK" ]; then
  say "Done. Open this link to create your admin account (works once, for 24 hours):"
  say ""
  say "  $LINK"
else
  say "Done. The admin console is at $URL/admin/"
  say "No admin yet? docker exec remoteman-relay gateway admin setup   (prints a one-time link)"
fi
say ""
[ "$MODE" = proxy ] && say "Point your proxy at http://<this box>:$PORT (WebSockets on, long timeouts) for $HOST."
say "Then in the console: add a site, paste its one-line connector install inside that network, and invite your PCs."
say "Upgrade later: cd $DIR && docker compose pull && docker compose up -d"
