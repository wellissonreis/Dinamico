#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/.."

if command -v dotnet >/dev/null 2>&1; then
  dinamico_dotnet="$(command -v dotnet)"
elif [[ -x /tmp/dinamico-sdk/dotnet ]]; then
  dinamico_dotnet=/tmp/dinamico-sdk/dotnet
else
  echo "Instale o SDK .NET 10.0.204 no Linux/WSL para iniciar o painel." >&2
  exit 1
fi
if ! command -v npm >/dev/null 2>&1; then
  echo "Instale Node.js 22.12+ e npm para compilar o React." >&2
  exit 1
fi
if [[ -z "${DINAMICO_DATABASE_CONNECTION:-}" ]]; then
  if [[ -n "${DINAMICO_POSTGRES_PASSWORD:-}" ]]; then
    echo "Defina DINAMICO_DATABASE_CONNECTION para usar sua senha personalizada." >&2
    exit 1
  fi
  export DINAMICO_DATABASE_CONNECTION="Host=127.0.0.1;Port=${DINAMICO_POSTGRES_PORT:-55432};Username=dinamico;Password=dinamico-local-m2;Database=dinamico_m2"
fi

docker compose -f compose.m2.yaml up -d --wait
npm ci --prefix FrontEnd
npm run build --prefix FrontEnd
"$dinamico_dotnet" restore Dinamico.slnx --locked-mode
"$dinamico_dotnet" build src/Dinamico.Web/Dinamico.Web.csproj -c Release --no-restore
echo "Abra http://127.0.0.1:5080 — Ctrl+C encerra o painel e interrompe a execução ativa."
exec "$dinamico_dotnet" src/Dinamico.Web/bin/Release/net10.0/Dinamico.Web.dll
