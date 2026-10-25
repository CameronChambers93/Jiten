#!/usr/bin/env bash
# Runs the Jiten Nuxt dev server in the anki-mining dev container, reachable from the host at https://localhost:3000.
# The dev script sets NODE_TLS_REJECT_UNAUTHORIZED=0, so SSR accepts the API's self-signed cert.
cd "$(dirname "$0")/../Jiten.Web"
exec pnpm dev --host 0.0.0.0 --port 3000
