#!/usr/bin/env bash
# Runs the Jiten Nuxt dev server in the anki-mining dev container, reachable from the host at https://localhost:3000.
# The dev script sets NODE_TLS_REJECT_UNAUTHORIZED=0, so SSR accepts the API's self-signed cert.
# For phone access over the LAN, pass the host PC's LAN IP: JITEN_LAN_IP=192.168.1.20 scripts/dev-web.sh
# The browser then calls the API at that IP instead of localhost (SSR uses it too, through the host's port mapping).
cd "$(dirname "$0")/../Jiten.Web"
if [ -n "$JITEN_LAN_IP" ]; then
  export NUXT_PUBLIC_BASE_URL="https://$JITEN_LAN_IP:7299/api/"
fi
exec pnpm dev --host 0.0.0.0 --port 3000
