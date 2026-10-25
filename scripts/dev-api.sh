#!/usr/bin/env bash
# Runs the Jiten API in the anki-mining dev container, reachable from the host at https://localhost:7299.
# Uses the Nuxt dev cert (Jiten.Web/localhost.pem, see DEVELOPMENT.md); binds 0.0.0.0 for the compose port mapping.
cd "$(dirname "$0")/.."
export ASPNETCORE_ENVIRONMENT=Development
# Key ring for encrypted admin settings (translation API keys); the compose file sets it too, from the next rebuild.
export DataProtectionKeysPath=${DataProtectionKeysPath:-/data/dataprotection-keys}
export ASPNETCORE_Kestrel__Certificates__Default__Path=$PWD/Jiten.Web/localhost.pem
export ASPNETCORE_Kestrel__Certificates__Default__KeyPath=$PWD/Jiten.Web/localhost-key.pem
exec dotnet run --project Jiten.Api/Jiten.Api.csproj --urls "https://0.0.0.0:7299"
