#!/usr/bin/env bash
# Installs the feature workflow's tools under /worktrees/.tools, which survives sessions:
# Playwright with headless Chromium (screenshots) and dotnet-ef (migrations). Safe to rerun.
set -euo pipefail
TOOLS=/worktrees/.tools
mkdir -p "$TOOLS/pw"

if [ ! -d "$TOOLS/pw/node_modules/playwright" ]; then
  (cd "$TOOLS/pw" && npm init -y >/dev/null && npm i playwright@1.61.0 >/dev/null)
fi
# /opt/pw-browsers isn't writable in the container, so browsers live beside the package.
PLAYWRIGHT_BROWSERS_PATH="$TOOLS/pw/browsers" "$TOOLS/pw/node_modules/.bin/playwright" install chromium >/dev/null

if [ ! -x "$TOOLS/ef/dotnet-ef" ]; then
  # Match Microsoft.EntityFrameworkCore.Design in Jiten.Core.csproj.
  dotnet tool install dotnet-ef --version 9.0.2 --tool-path "$TOOLS/ef" >/dev/null
fi
[ -f "$TOOLS/jiten-login.env" ] || echo "no login file: create $TOOLS/jiten-login.env (mode 600) with JITEN_USER= and JITEN_PASSWORD= lines"
echo "tools ready in $TOOLS"
