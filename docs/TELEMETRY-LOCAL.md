# Telemetria local opcional

Este laboratório adiciona um Jaeger descartável e um **alvo de demonstração instrumentado**, separado da aplicação do usuário e do painel Dinamico. Não instrumenta automaticamente sua aplicação real. O alvo expõe uma operação controlada com `Task.Delay`; o span da dependência não representa uma chamada SQL ou HTTP real.

Requisitos: Docker com Compose v2 que aceite `configs.content`, curl e o SDK fixado em [global.json](../global.json). Execute da raiz do repositório:

```bash
bash scripts/run-telemetry-demo.sh
```

O script restaura em locked mode e compila somente `tools/Dinamico.TelemetryDemo`, inicia o Compose e espera até 30 segundos pela saúde HTTP. Em seguida mantém o alvo em primeiro plano. Prefere `dotnet` Linux; também aceita `dotnet.exe` no WSL e encaminha as variáveis via `WSLENV`. Não inicia nem recompila a API/painel. Portas ocupadas devem ser liberadas pelo proprietário; não substitua servidores existentes.

| Endpoint local | Uso |
| --- | --- |
| `http://127.0.0.1:5095/work?delayMs=150` | Alvo HTTP; demora de 0 a 2.000 ms, padrão 150 ms; valores fora do limite retornam 400 |
| `http://127.0.0.1:5095/health` | Saúde, contador cumulativo de chamadas `/work` e memória atual do processo alvo |
| `http://127.0.0.1:4318/v1/traces` | Entrada OTLP/HTTP, JSON do gerador ou protobuf do SDK do alvo |
| `http://127.0.0.1:16686` | Interface e API de consulta Jaeger |
| `http://127.0.0.1:13133/status` | Saúde do Jaeger; exige HTTP 200 e `healthy: true` |

Todos os binds publicados ficam em loopback. O painel continua usando sua configuração habitual (5080); o PostgreSQL de `compose.m2.yaml` em 55432 é independente. Para Windows/WSL, alvo, gerador e Docker precisam alcançar os mesmos endereços de loopback. A demonstração foi validada usando `dotnet.exe` no WSL com loopback compartilhado. Em WSL com rede NAT sem esse compartilhamento, execute alvo e gerador no mesmo host Windows, ou use ambos no Linux com Docker acessível; não troque os binds por `0.0.0.0`.

## Habilitar no host e executar

As variáveis exportadas pelo script só existem no processo dele. No **terminal que inicia a API ou CLI**, configure antes de iniciar o processo:

```bash
export DINAMICO_OTLP_TRACES_ENDPOINT='http://127.0.0.1:4318/v1/traces'
export DINAMICO_TRACE_URL_TEMPLATE='http://127.0.0.1:16686/trace/{traceId}'
# Necessário se o host/CLI usar dotnet.exe a partir do WSL:
export WSLENV="${WSLENV:+$WSLENV:}DINAMICO_OTLP_TRACES_ENDPOINT:DINAMICO_TRACE_URL_TEMPLATE"
```

No PowerShell, as variáveis equivalentes são:

```powershell
$env:DINAMICO_OTLP_TRACES_ENDPOINT = 'http://127.0.0.1:4318/v1/traces'
$env:DINAMICO_TRACE_URL_TEMPLATE = 'http://127.0.0.1:16686/trace/{traceId}'
```

Reinicie somente o host Dinamico que você administra para ele receber esse ambiente. Na interface, selecione seu projeto e use URL `http://127.0.0.1:5095/work?delayMs=150`, GET, 5 req/s, 2 segundos e concorrência 4. Em **Captura de requisições**, escolha **Completa · até 2.000 planejadas** (`Complete`) e marque **Correlacionar requisições com traces** (`correlateRequests: true`). Execute; abra um detalhe capturado e seu link de trace. A tabela deve mostrar dez operações e o trace deve conter `load.request`, `GET /work` e `controlled-dependency.delay`, com a maior parte do tempo do endpoint na operação controlada.

Alternativa pela CLI já compilada, trocando `dotnet` por `dotnet.exe` quando aplicável:

```bash
dotnet src/Dinamico.LoadEngine.Local/bin/Release/net10.0/Dinamico.LoadEngine.Local.dll run \
  --config tools/Dinamico.TelemetryDemo/load.json \
  --output tools/Dinamico.TelemetryDemo/bin/telemetry-report.json --overwrite
```

O exemplo é finito: dez intenções planejadas, captura completa, nenhuma política de retry HTTP. Compare `requestCount` de `/health` antes/depois para reconciliar chamadas no alvo, sem outras chamadas concorrentes. O contador reinicia com o alvo e conta invocações do handler `/work`, incluindo cancelamento ou demora fora do limite; erros de binding anteriores ao handler (como `delayMs=abc`) e `/health` não entram. `targetWorkingSetBytes` e `targetManagedHeapBytes` são snapshots explícitos do alvo, não séries históricas nem métricas armazenadas no Jaeger. Os gráficos de recursos do Dinamico continuam medindo o gerador.

Consulte um trace pelo ID do detalhe ou relatório; o endpoint só responde depois da exportação em lote (aguarde até alguns segundos):

```bash
curl --noproxy '*' -fsS http://127.0.0.1:13133/status
curl --noproxy '*' -fsS http://127.0.0.1:5095/health
curl --noproxy '*' -fsS http://127.0.0.1:16686/api/traces/SEU_TRACE_ID
```

Confira IDs, além dos nomes: `GET /work.references[].spanID` deve ser o `spanId` de `load.request`; `controlled-dependency.delay.references[].spanID` deve ser o span do endpoint. Uma consulta vazia indica que esses spans ainda não chegaram ou foram descartados. `exportState: exported` confirma a aceitação dos spans do **gerador**, sem garantir retenção eterna nem exportação do alvo.

Com API/painel e alvo já iniciados, também é possível executar `node scripts/validate-telemetry.mjs`. No WSL usando o host Windows, use `cmd.exe /c node scripts/validate-telemetry.mjs`, mantendo API, alvo e script no mesmo loopback. Reserve o contador do alvo para esse ensaio: não envie outras chamadas `/work` em paralelo. O script confere a cadeia no Jaeger e executa o perfil de 1.000 req/s por cinco segundos, concorrência 32 e demora controlada de 64 ms para evidenciar descarte por concorrência; grava a evidência em `TestResults/Diagnostics/Telemetry/verified.json`. A validação local mediu seis requests correlacionados e, no perfil limitado, 2.092 iniciados/recebidos/HTTP 200 e 2.908 descartados de 5.000 planejados (2.905 por concorrência, três por scheduler), com dados completos. Esses valores dependem da máquina; não são garantia de capacidade.

O smoke final com os binários atualizados confirmou novamente seis requests correlacionados, os três spans no Jaeger e ausência do canário no relatório/traces. No perfil limitado foram 2.080 iniciadas/recebidas e 2.920 não iniciadas (2.915 por concorrência, cinco por scheduler). Essa segunda evidência está em `TestResults/Diagnostics/Telemetry/verified-final.json`, selecionada por `DINAMICO_TELEMETRY_EVIDENCE`; ambas as execuções reconciliaram todas as contagens.

## Limites, opt-out e encerramento

[compose.telemetry.yaml](../compose.telemetry.yaml) fixa Jaeger 2.16.0 all-in-one, até 2.000 traces em memória, sem volume, limite de 512 MiB, um núcleo e processador de memória de 384 MiB. A retenção é por quantidade, sem garantia de duração; reinício/remoção perde os traces. Pressão de memória pode rejeitar telemetria. É um backend local descartável, sem autenticação, destinado a ensaios pequenos. O script verifica `/status` externamente porque a imagem não fornece curl/shell para um healthcheck de Compose.

O alvo usa OpenTelemetry .NET 1.20.1 com lockfile e uma fonte própria. Exporta somente nomes fixos, método/rota/status, tipo da operação e demora numérica; não exporta URL, query, headers, payload, baggage ou mensagens de erro. Lê apenas o `traceparent` W3C para formar o pai remoto. Fila de 512 spans, lotes de 128, exportação a cada 500 ms e timeout de um segundo tornam a coleta finita; com excesso de tráfego pode haver descarte. `/health` não gera spans. O endpoint OTLP do alvo precisa ser HTTP em loopback e não aceita credenciais/query/fragmento. Jaeger recebe traces; este laboratório não instala backend de métricas ou logs.

Para verificar opt-out, mantenha `Complete` e desmarque a correlação. O gerador deve informar `disabled` e não incluir IDs. O alvo usa sampling baseado no pai: coleta pais W3C amostrados (`01`) e não exporta novas raízes quando o request não carrega contexto; isso evita preencher a memória com tráfego sem correlação. Para verificar coletor indisponível sem interromper outros ensaios, configure **somente a CLI desse ensaio** com `DINAMICO_OTLP_TRACES_ENDPOINT=http://127.0.0.1:14318/v1/traces` (porta sem serviço). A execução deve preservar contagens e captura, com `exportState: failed`; esse teste não desliga o backend de outros processos.

Ctrl+C encerra o alvo. Para parar e descartar somente este laboratório:

```bash
docker compose -f compose.telemetry.yaml down
```

Não use `docker compose down` em arquivos/projetos de outros serviços. O script não encerra automaticamente Jaeger ao parar o alvo, permitindo inspecionar os traces depois.

Configuração baseada nas fontes oficiais: [Jaeger 2.16 deployment e saúde](https://www.jaegertracing.io/docs/2.16/deployment/), [configuração de memória/receivers da versão fixa](https://github.com/jaegertracing/jaeger/blob/v2.16.0/cmd/jaeger/internal/all-in-one.yaml), [exportadores OpenTelemetry .NET](https://opentelemetry.io/docs/languages/dotnet/exporters/). Para instrumentar sua aplicação e interpretar limitações, veja [WEB-LOCAL](WEB-LOCAL.md#correlação-e-exportação-opcional).
