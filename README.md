# Dinamico — Load Engine

**M2 entregue em 2026-10-22:** PostgreSQL, importação idempotente, consultas e retenção local. Instruções e limites em [Persistência local — M2](docs/M2-LOCAL-PERSISTENCE.md). O estado M1 e suas medições abaixo descrevem o motor HTTP; M3–M7 continuam futuros.

CLI local para carga HTTP/1.1 constante, com agenda open-loop e relatório JSON. Somente HTTP(S) com **IP loopback literal**, como `127.0.0.1` ou `[::1]`; `localhost`, DNS e destinos externos são recusados. Sem retries, redirects, proxy de ambiente, cookies ou descompressão automática. TLS conserva a validação de certificado.

Estado atual: componentes 001–009, observabilidade 010 e CLI/relatório 011 disponíveis; transporte 003 ainda parcial e gate de calibração 012 pendente. **M1 e FICHA-037 não estão concluídos.** Validação final: **541 testes aprovados**, 25 direcionados, build Release sem avisos/erros. As contagens de 515 testes da continuação 009 são históricas; detalhes e evidências em [FICHA-012](docs/implementation-cards/FICHA-012-ENGINE-VALIDATION.md).

## Executar contra sua aplicação

Requer SDK **10.0.204**, fixado em [global.json](global.json), e runtime .NET 10. Na raiz do repositório:

```sh
dotnet restore Dinamico.slnx --locked-mode
dotnet build Dinamico.slnx -c Release --no-restore
```

Inicie sua aplicação em outro terminal, escutando por exemplo em `http://127.0.0.1:5000`. Ajuste a porta e o caminho em [examples/local-http.json](examples/local-http.json):

```json
{
  "schemaVersion": 2,
  "url": "http://127.0.0.1:5000/",
  "requestsPerSecond": 10,
  "durationSeconds": 10,
  "maxConcurrency": 8,
  "requestTimeout": "00:00:02"
}
```

Execute o binário já compilado para separar resultado de mensagens do build:

```sh
dotnet src/Dinamico.LoadEngine.Local/bin/Release/net10.0/Dinamico.LoadEngine.Local.dll run --config examples/local-http.json --output report.json 2>run.jsonl
```

O relatório fica em `report.json`; logs JSON e amostras agregadas ficam em `run.jsonl`. Para repetir com o mesmo destino de arquivo, acrescente `--overwrite`. O diretório de saída deve existir. Sem essa opção, um arquivo existente é preservado e a CLI retorna 2 antes de enviar tráfego. Não use o caminho da configuração como saída.

No WSL com somente SDK Windows, substitua `dotnet` por `dotnet.exe` e mantenha arquivos/configuração no volume Windows. A validação POSIX desta entrega usou um SDK Linux separado; SIGTERM não se aplica ao processo Windows. Ctrl+C interrompe a geração, cancela operações ativas e tenta publicar o relatório. SIGTERM tem o mesmo comportamento em Linux. SIGKILL/crash não permitem essa finalização.

Para JSON em stdout, use `--output -` e redirecione stderr separadamente. Não misture mensagens do alvo ou de build com esse canal:

```sh
dotnet src/Dinamico.LoadEngine.Local/bin/Release/net10.0/Dinamico.LoadEngine.Local.dll run --config examples/local-http.json --output - >report.json 2>run.jsonl
```

## Configuração e segurança

`schemaVersion: 2` é obrigatório; campos desconhecidos, duplicados e enums desconhecidos são rejeitados. As opções de carga são as de [LoadTestConfiguration](src/Dinamico.LoadEngine/Contracts/LoadTestConfiguration.cs). Método default GET; `payload`, se necessário, é uma string base64; `requestTimeout` usa formato TimeSpan, como `00:00:02`. Arquivo de entrada limitado a 2 MiB antes da desserialização.

Limites do plano: 1–1.000 RPS, duração inteira 1–600 s, concorrência 1–256, timeout positivo até 60 s, payload até 64 KiB, resposta até 1 MiB, headers até 16 KiB. O limite de corpo também vale sem Content-Length. Defaults não informados: 100 RPS, 30 s, concorrência 64 e timeout 10 s; prefira os valores explícitos conservadores do exemplo.

Credenciais ficam no arquivo local, nunca em argumentos. Use somente valores sintéticos no laboratório HTTP. Todos os headers são sensíveis. Nem valores nem nomes personalizados de headers, URL/path/query, payload ou mensagens livres de exceptions são publicados. O snapshot é uma projeção segura, sem reprodução de credenciais. Proteja o arquivo de entrada conforme seu ambiente.

`rateLimitMode` aceita `Normal`, `Authorized` e `Diagnostic`; os três preservam o mesmo perfil. Diagnostic não sonda. Authorized exige `testCredentialHeaderIndex` válido e `testAuthorizationDeclared: true`; é uma declaração local, sem comprovar autorização ou remover limites. Exemplo de campos adicionais com credencial **sintética**:

```json
{
  "rateLimitMode": "Authorized",
  "headers": [{ "name": "Authorization", "value": "Bearer synthetic-only" }],
  "testCredentialHeaderIndex": 0,
  "testAuthorizationDeclared": true
}
```

Incorpore esses campos ao objeto completo do exemplo; esse trecho isolado não é uma configuração válida. `retryPolicy` admite somente `None`; `successRule`, somente `Completed2xxV1`.

## Ler o relatório

O relatório tem `schemaVersion: 1`, independente do `plan.schemaVersion: 2`. `windows` contém deltas por intervalos de 5 s, incluindo drain e janela parcial final; `result.totals` consolida o prefixo confirmado. Não some `result.totals` às janelas.

| Campo | Interpretação |
|---|---|
| `result.plannedTotal` | Carga solicitada pelo plano |
| `result.scheduledDue` | Chegadas vencidas até a parada da geração |
| `result.notScheduledAfterStop` | Trecho não executado após parada, separado de drops |
| `result.totals.counters.Started` | Operações iniciadas; não comprova recebimento pelo servidor |
| `SchedulerLate`, `ConcurrencyLimit`, `Policy` | Drops exclusivos de geração |
| `HttpCompleted` | Corpo completo, inclusive HTTP 4xx/5xx |
| `HttpSuccess` | Subconjunto completo 200–299; 202 não prova conclusão de negócio |
| `TransportFailed`, `RequestTimedOut`, `CancelledInFlight`, `ResponseLimitExceeded`, `InternalFailed` | Terminais exclusivos fora de HTTP completo |
| `result.totals.statuses` | Headers finais observados, contados uma vez antes do corpo |
| `result.quality`, `dataComplete`, `coverage`, `failures`, `reason` | Qualidade, cobertura/flush e causa de término, independentes da saúde HTTP |

No total fechado completo, `scheduledDue = Started + SchedulerLate + ConcurrencyLimit + Policy`, `plannedTotal = scheduledDue + notScheduledAfterStop` e `Started = lastInFlight + soma dos terminais`. Em cobertura parcial, são totais **observados**, sem extrapolação. As identidades não precisam valer isoladamente por janela.

`httpCompletedDuration` é a população principal de latência observada pelo cliente. `terminalDuration` também inclui duração até timeout/cancelamento. Ambas publicam `count` (N), soma, min/max, buckets, P50/P95/P99, unidade em nanossegundos, aproximação e `overflowCount`. Percentis sem população são `null`. Cada quantil informa `upperBoundNanoseconds` e `isOverflow`; overflow não tem limite finito. Schema `log2-32-v1`: acima de 1 µs, erro de excesso menor que aproximadamente 2,19% + 1 ns; primeiro bucket até 1 µs; overflow acima de 67,108864 s. Percentis são derivados dos buckets somados, nunca médias dos percentis das janelas.

`rateLimitedPercent` usa `status 429 / finalStatuses`, ou null sem headers finais. Um 429 com falha de corpo mantém esse sinal e não entra em HTTP completo. 503 com Retry-After não ativa rate limiting. Categorias Retry-After: `NotObserved` = captura não realizada; `Missing` = captura efetiva sem campo em 429; `NotApplicable` = outro status; `Invalid`, `DeltaSeconds`, `HttpDate`. Há clamp, incerteza civil e min/max de delay válido, nulos sem valores. Ausência de 429 não prova ausência de limites; cobertura insuficiente torna diagnóstico inconclusivo.

`durationsSeconds` separa geração, drain, flush e total do controlador. Para RPS médio iniciado, divida Started pelo tempo de geração; para throughput por janela, divida HttpCompleted pela largura real dessa janela. Não trate latência cliente como latência exclusiva do servidor. `GeneratorLimited` e drops mostram que a carga não foi atingida, mesmo com percentis baixos.

## Saída, completude e códigos de retorno

| Código | Significado |
|---|---|
| 0 | Protocolo concluído e dados completos; pode haver HTTP 500, erros de transporte ou drops |
| 2 | Argumentos/configuração inválidos ou conflito de saída |
| 3 | Falha interna, saída/flush ou dados parciais |
| 130 | Cancelamento solicitado e relatório publicado; falha de publicação prevalece como 3 |

A saída mantém o contrato `ILocalMetricOutput`: confirmação por janela e flush explícito. Há no máximo seis janelas não confirmadas; pressão de métricas interrompe a geração e marca dados parciais. Criação/publicação têm deadlines; saída bloqueada não mantém o processo vivo indefinidamente. Um pipe fechado retorna falha.

Arquivo dedicado é escrito em `report.json.incomplete-<id>` no mesmo diretório e movido para o destino após concluir. Temporários podem ficar em falha/crash; não os interprete como relatório completo. Em stdout, um prefixo truncado também não é relatório final. Flush não promete resistência a queda de energia (`fsync`) nem rename atômico em todo filesystem. Considere código de retorno, parse JSON e `dataComplete` conjuntamente.

Observabilidade: fila de 32 linhas, linha até 4 KiB; amostras agregadas até 5/janela, 100/execução e 512 bytes. Fingerprints categoria/status têm intervalo mínimo de 30 s; resumos de processo seguem janelas. Contadores permanecem completos mesmo se amostras/logs forem suprimidos. Categorias terminais têm prioridade sobre tempestades HTTP. `observability` informa supressão, descartes e falha da saída de logs. Erros de transporte são agregados, sem distinguir DNS/TLS no relatório local. Não há logs por request.

## Verificação e limites atuais

```sh
dotnet test Dinamico.slnx -c Release --no-build --no-restore --filter 'FullyQualifiedName~CliProcessTests|FullyQualifiedName~BoundedObservabilityTests' --logger trx --results-directory TestResults/Ficha011/Directed
dotnet test Dinamico.slnx -c Release --no-build --no-restore --logger trx --results-directory TestResults/Ficha011/Suite
```

Os testes CLI iniciam a [fixture Kestrel real](tests/Dinamico.LoadEngine.Tests/Fixtures/ControlledHttpFixture.cs) em porta loopback aleatória e executam o binário em subprocesso. Não é necessário manter um serviço de fixture: o próprio teste cria/encerra o alvo e compara seus recebimentos com o relatório. Incluem 202/429/503, corpos limitados, alvo lento, sinais POSIX, canários, arquivo existente e falha/pressão de saída. Os testes POSIX só se aplicam ao runtime Linux.

Para repetir os probes de 10/100 RPS por 30 s contra a fixture, após compilar a CLI:

```sh
dotnet run --project tools/Dinamico.LocalValidation -c Release -- .
```

Os relatórios/contagens recebidas/ambiente/hash da projeção segura ficam em `TestResults/Ficha012/Smoke/`. A flag `functionalPassed` verifica conciliação; `nonSaturatedGatePassed` verifica separadamente o gate sem drops. Os primeiros probes mostraram 22/300 e 237/3.000 drops por atraso, com `GeneratorLimited`; o fluxo e os dados completos não representam aceite de capacidade. Resultados finais desta entrega na [012](docs/implementation-cards/FICHA-012-ENGINE-VALIDATION.md).

Warm-up separado, três repetições, CPU restrita, plateau de recursos em 2/10 minutos e envelope medido continuam pendentes. CPU/RSS/heap são amostras operacionais locais, sem isolamento de interferência entre gerador e alvo. Alocações, pausas GC, sockets/fila de handler e throttling não são medidos nesta entrega. Sem eles, não há evidência para encerrar M1 ou afirmar capacidade de produção.

Arquitetura e semântica canônica: [03](docs/03-LOAD-ENGINE-DESIGN.md), [ADRs](docs/adr/README.md), [fichas](docs/implementation-cards/README.md). Evidências de 119/455/496/515 testes e do host informativo são registros históricos, preservados nas fichas; não descrevem a CLI atual. Não foram incluídos banco, Control Plane, Kubernetes, React ou ingestão distribuída.
