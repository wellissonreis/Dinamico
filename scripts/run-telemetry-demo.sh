#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/.."

if command -v dotnet >/dev/null 2>&1; then
  dinamico_dotnet="$(command -v dotnet)"
elif command -v dotnet.exe >/dev/null 2>&1; then
  dinamico_dotnet="$(command -v dotnet.exe)"
elif [[ -x /tmp/dinamico-sdk/dotnet ]]; then
  dinamico_dotnet=/tmp/dinamico-sdk/dotnet
else
  echo 'Instale o SDK .NET definido em global.json.' >&2
  exit 1
fi
command -v curl >/dev/null
command -v docker >/dev/null

export DINAMICO_OTLP_TRACES_ENDPOINT="${DINAMICO_OTLP_TRACES_ENDPOINT:-http://127.0.0.1:4318/v1/traces}"
dinamico_default_trace_url='http://127.0.0.1:16686/trace/{traceId}'
export DINAMICO_TRACE_URL_TEMPLATE="${DINAMICO_TRACE_URL_TEMPLATE:-$dinamico_default_trace_url}"
if [[ "$dinamico_dotnet" == *.exe ]]; then
  # WSL variables must cross into the Windows process; values are URLs, not paths.
  export WSLENV="${WSLENV:+$WSLENV:}DINAMICO_OTLP_TRACES_ENDPOINT:DINAMICO_TRACE_URL_TEMPLATE"
fi

"$dinamico_dotnet" restore tools/Dinamico.TelemetryDemo/Dinamico.TelemetryDemo.csproj --locked-mode
"$dinamico_dotnet" build tools/Dinamico.TelemetryDemo/Dinamico.TelemetryDemo.csproj -c Release --no-restore --no-dependencies
docker compose -f compose.telemetry.yaml up -d
dinamico_ready=false
for ((attempt = 0; attempt < 30; attempt++)); do
  if curl --noproxy '*' --max-time 1 -fsS http://127.0.0.1:13133/status >/dev/null; then
    dinamico_ready=true
    break
  fi
  sleep 1
done
if [[ "$dinamico_ready" != true ]]; then
  echo 'Jaeger indisponível: veja docker compose -f compose.telemetry.yaml logs.' >&2
  exit 1
fi
echo 'Jaeger: http://127.0.0.1:16686 — alvo: http://127.0.0.1:5095/work?delayMs=150'
echo 'Configure também as duas variáveis DINAMICO no terminal do host/CLI. Veja docs/TELEMETRY-LOCAL.md.'
echo 'Ctrl+C encerra somente o alvo. Para descartar traces: docker compose -f compose.telemetry.yaml down'
exec "$dinamico_dotnet" tools/Dinamico.TelemetryDemo/bin/Release/net10.0/Dinamico.TelemetryDemo.dll
