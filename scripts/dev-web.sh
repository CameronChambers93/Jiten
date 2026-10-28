#!/usr/bin/env bash
# Runs the Jiten Nuxt dev server in the anki-mining dev container, reachable from the host at https://localhost:3000.
# The dev script sets NODE_TLS_REJECT_UNAUTHORIZED=0, so SSR accepts the API's self-signed cert.
# For phone access over the LAN, pass the host PC's LAN IP: JITEN_LAN_IP=192.168.1.20 scripts/dev-web.sh
# The browser then calls the API at that IP instead of localhost (SSR uses it too, through the host's port mapping).
# By default the browser calls the API through the anki-mining dev container's Caddy proxy, on the page's own origin
# (https://jiten.local/api/). Name another proxy with JITEN_HOST, or pass JITEN_HOST= for the API at localhost:7299.
cd "$(dirname "$0")/../Jiten.Web"
JITEN_HOST=${JITEN_HOST-jiten.local}
if [ -n "$JITEN_LAN_IP" ]; then
  export NUXT_PUBLIC_BASE_URL="https://$JITEN_LAN_IP:7299/api/"
elif [ -n "$JITEN_HOST" ]; then
  export NUXT_PUBLIC_BASE_URL="https://$JITEN_HOST/api/"
fi
exec pnpm dev --host 0.0.0.0 --port 3000
