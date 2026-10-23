# FICHA-012 — Calibração e aceite do primeiro marco

Prioridade: P0. Complexidade: M. Marco: M1. Status: concluída em 2026-10-21 no escopo HTTP/1.1 local loopback, com envelope Windows de 10 RPS medido e limitações WSL explícitas. Os resultados de 2026-10-17 abaixo são históricos.

## Objetivo

Demonstrar correção do motor integrado e estabelecer um envelope local medido de capacidade, sem inferir metas universais de RPS.

## Contexto arquitetural

Gate de M1 antes de adicionar banco/CP/Kubernetes. Usa critérios de [08](../08-DEVELOPMENT-ROADMAP.md); resultado da ficha é evidência reproduzível e correções necessárias dentro do motor.

## Conceitos necessários

Benchmark é condicionado por hardware/perfil. CPU do cliente, rede e alvo podem limitar o mesmo experimento. Warm-up precisa ser separado e identificado.

## Responsabilidades

Executar matriz funcional/de falhas, medir CPU/RSS/alocações/sockets/lateness, verificar contagens/percentis e documentar limitações. Corrigir falhas de M1 encontradas e repetir apenas casos afetados e gates essenciais.

## Fora do escopo

Prometer 50k RPS, escalar Minikube, comparar marketing de ferramentas, introduzir engine pronta ou publicar serviço.

## Dependências

FICHA-011; suas dependências transitivas incluem todas as fichas anteriores do marco.

## Contratos

Relatório de validação com ambiente, versões, parâmetros, hash de configuração sanitizada, evidências, repetições, variabilidade e resultado por critério. Distinguir calculado/medido/hipótese. Nenhuma credencial no artefato.

## Registro histórico da proposta — aceite de Rate Limiting v2

Gate integrado continua pendente; o recorte atual de contratos/parser não encerra M1 nem a FICHA-037. Seguir o [contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12). Evidências atuais estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md); 119 testes da base são históricos. Esta atualização não executa testes nem mede capacidade.

Matriz futura de M1: 429 contínuo/intermitente e após falha de corpo; 503 com Retry-After sem IsRateLimited; NotObserved sem captura versus Missing após captura efetiva sem campo em 429, NotApplicable em outros status e associações inválidas; parser inválido/duplicado/datas/clamp; 200/202 completo e 2xx incompleto; credenciais sintéticas em HTTP loopback nos três modos, sem validação de origem da credencial nem distinção entre segredos reais e sintéticos pelo validador, e Authorized ainda limitado; canários sem vazamento; agenda open-loop idêntica sem retries/sondagens; status contado uma vez; denominador zero/null, fronteiras/drain, categorias fixas/min-max e cobertura insuficiente explícita. Conciliar recebimentos da fixture com inícios e terminais, sem inferir recebimento pelo servidor a partir do cliente.

Registrar resultados e overhead somente após execução real, com ambiente, limites e populações. Integração parcial não autoriza marcar a 037 pronta. Ensaios NAT/egress/IP compartilhado e falhas/replay/intervalo global distribuído são gates somente de M4/M5; cofre/referências e UI/SSE pertencem a M3 e M6. Não simular esses marcos como evidência de produção.

## Decisões técnicas

Modo Release e fixture local real, relógio falso para exatidão e métricas do runtime para recursos. Usar ferramentas adicionais de profiling só para uma hipótese específica; registrar seu overhead.

## Passos de implementação

1. Executar vetores determinísticos de agenda/histograma/invariantes.
2. Aquecer separadamente e iniciar cenários 10/100 RPS por 30 s.
3. Repetir cenário estável pelo menos três vezes, registrar dispersão.
4. Testar alvo lento, falhas, limite de corpo e gerador limitado.
5. Ampliar duração para 2 e 10 min observando plateau; ampliar RPS só com folga.
6. Testar Ctrl+C, SIGTERM, sink bloqueado e falha de gravação em subprocesso.
7. Registrar envelope e corrigir/retestar falhas antes de fechar M1.

## Testes obrigatórios

Todos os gates das fichas anteriores, incluindo erro de percentil, contagem da fixture, no máximo C em voo, nenhuma exceção não observada, limite de buffers e redaction. Cenário sem saturação proposto: inícios dentro de ±2% do plano, zero drops; simulação temporal exige contagem exata. Sob saturação, drops/flags obrigatórios e memória limitada, sem exigência de atingir RPS.

## Observabilidade

Guardar sumário de CPU, RSS, GC/alocações, pool/conexões, atraso de dispatch e fila de saída, com métricas indisponíveis marcadas. Não produzir dumps com secrets sem necessidade.

## Critérios de aceite

Correção matemática e ciclo de recursos aprovados; falhas não geram sucesso silencioso. Pelo menos um cenário local não saturado cumpre o gate e um cenário limitado revela a limitação. Há evidência de memória limitada e relatório reproduzível, sem afirmar que resultado local representa produção.

## Riscos e trade-offs

Mesmo host confunde cliente/alvo; se não houver host separado, declarar diagnóstico inconclusivo para rede/capacidade externa. Falha de gate exige investigação, não reduzir exigência retrospectivamente sem revisão.

## Definição de pronto

- [x] Evidências e parâmetros reproduzíveis registrados.
- [x] Correções necessárias de M1 concluídas e verificadas.
- [x] Envelope medido e limitações revisados antes de detalhar M2.

## Validação funcional atual — 2026-10-17

Ambiente medido: Ubuntu 24.04, WSL2 Linux `6.6.87.2-microsoft-standard-WSL2`, x86_64; SDK 10.0.204, MSBuild 18.3.3, runtime .NET/ASP.NET Core 10.0.8. SDK Linux instalado em `/tmp/dinamico-sdk` para testar sinais reais; configurações locais `.codex/`, AGENTS.md e CODEX.md preexistentes foram preservadas fora dos commits. Não há bibliotecas novas de carga/histograma. Gerador e fixture competem pelo mesmo host; não há isolamento de CPU/rede.

Comandos executados na raiz (prefixo `PATH=/tmp/dinamico-sdk:$PATH` neste WSL; em instalação normal basta `dotnet`):

```sh
dotnet restore Dinamico.slnx --locked-mode
dotnet build Dinamico.slnx -c Release --no-restore
dotnet test Dinamico.slnx -c Release --no-build --no-restore --filter 'FullyQualifiedName~CliProcessTests|FullyQualifiedName~BoundedObservabilityTests' --logger trx --results-directory TestResults/Ficha011/DirectedRelease
DINAMICO_EVIDENCE_DIRECTORY="$PWD/TestResults/Ficha011/Scenarios" dotnet test Dinamico.slnx -c Release --no-restore --logger trx --results-directory TestResults/Ficha011/FinalSuite
dotnet run --project tools/Dinamico.LocalValidation -c Release -- .
```

Resultado final: **541 testes aprovados**, zero falhas/ignorados, em `TestResults/Ficha011/FinalSuite/` (TRX `_Well-PC_2026-10-17_18_51_13.trx`). Recorte direcionado após as correções: **25 aprovados**, zero falhas/ignorados, em `TestResults/Ficha011/DirectedRelease/`; o caso específico adicional de HTTP 500 foi incluído em seguida e aprovado na suíte final de 541. Build Release com **zero avisos/erros**, restore locked-mode aprovado; `git diff --check` aprovado. Os 515/97 da 009 e 540/25 das rodadas intermediárias são evidências históricas.

O host para subprocessos foi selecionado por `DOTNET_HOST_PATH=/tmp/dinamico-sdk/dotnet`. Os comandos com atribuição de variável são Bash; no PowerShell use `$env:DINAMICO_EVIDENCE_DIRECTORY` se quiser guardar os exemplos (opcional). Em Windows, os testes POSIX não executam sinais; a evidência acima é Linux. Não houve validação final completa do runtime Windows nesta continuação.

Resultados funcionais e de falha: fixture real em subprocesso para 202/429/503 nos três modos e 500 completo com exit 0; status nos headers contado uma vez mesmo após limite de corpo/timeout; Retry-After inválido, duplicado e clamp; conciliação com recebimentos; null sem população/denominador; canários de URL/header/payload/Retry-After e exception; erro seguro limitado mesmo com 128 headers inválidos; overwrite opt-in; diretório ausente; stdout JSON; SIGINT/SIGTERM com 130 e operações canceladas/relatório completo; pipe fechado e pipe de 4 KiB bloqueado com código 3 e limite de tempo. Os histogramas matemáticos/overflow e os deadlines/corridas de sink seguem cobertos pela suíte anterior, agora reexecutada. Não foi provocado overflow de duração real >67 s no subprocesso.

Falhas encontradas e correções: ConsoleStream ocultava EPIPE e retornava 0; saída agora usa FileStream sobre o handle padrão e o teste passa com 3. Primeiros testes funcionais com somente duas chegadas podiam ter ambas descartadas por atraso no WSL; os vetores de corpo/timeout agora oferecem 20 chegadas e a coleção CLI não disputa CPU com outras classes. Isso não relaxa nenhuma identidade nem o gate de performance: drops continuam contabilizados, e o gate sem saturação abaixo continua reprovado/aberto. O primeiro teste de supressão também foi corrigido para avançar o TimeProvider falso junto das janelas (não dependia de espera civil real).

A saída de `DINAMICO_EVIDENCE_DIRECTORY` contém relatórios e stderr sanitizados. Por exemplo, `202-Normal.json` confirma HTTP completo/sucesso; `429-Authorized.json` confirma HTTP completo sem sucesso; `503-Diagnostic.json` não classifica Retry-After como rate limiting; `429-timeout.json` mantém os headers e todos os terminais de timeout fora da população HTTP completa. Os resultados exatos podem variar por drops e estão nos artefatos, sem pressupor que inícios garantam recebimento.

## Probe reproduzível da fixture — parcial, sem aceite de capacidade

[tools/Dinamico.LocalValidation](../../tools/Dinamico.LocalValidation/Program.cs) compila a mesma fixture Kestrel da 002, cria configurações locais seguras e chama a CLI real. Executa **uma** repetição de 10 RPS/30 s e uma de 100 RPS/30 s, status 202, concorrência 32, timeout 2 s, corpo 16 bytes. Cada subprocesso tem limite de 90 s. Sem warm-up separado; não são resultados de envelope calibrado. O utilitário não entra na solução de produção e não é dependência do motor.

Artefatos em `TestResults/Ficha012/Smoke/`: `*.config.json` sem credenciais, `*.report.json`, `*.jsonl`, `*.summary.json` com ambiente, SHA-256 da projeção sanitizada e flags `functionalPassed`/`nonSaturatedGatePassed`. Retorno do probe verifica protocolo/conciliação, sem mascarar o gate de capacidade: este fica numa flag independente. O destino da configuração contém somente IP/porta efêmera da fixture.

Os primeiros ensaios desta continuação tiveram 278/300 inícios/conclusões a 10 RPS (22 scheduler_late) e 2.763/3.000 a 100 RPS (237 scheduler_late), zero drops de concorrência e contagem recebida igual aos inícios. Dados completos e `GeneratorLimited`, exit 0. Esses números são medições locais, não uma promessa de RPS nem evidência causal de qual recurso limitou. Uma repetição posterior pelo utilitário versionado está registrada abaixo.

## Resultado do utilitário versionado — execução final

Release, motor/CLI dos commits `ae415e2` e `185a631`; 16 processadores lógicos reportados pelo runtime (quota de CPU desconhecida). Comando: `dotnet run --project tools/Dinamico.LocalValidation -c Release -- .`, retorno 0 por conciliação funcional. Os dois cenários têm `functionalPassed=true`, `nonSaturatedGatePassed=false`, `dataComplete=true`, `GeneratorLimited`, zero terminais de erro e zero drops de concorrência/policy. Não há interpretação causal automática.

| Perfil | Planejado/due | Iniciado = recebido = completo = sucesso | Scheduler late | P50/P95/P99 HTTP completo (ms, limite superior aproximado) | N |
|---|---:|---:|---:|---|---:|
| 10 RPS, 30 s | 300 | 213 | 87 | 0,724078 / 1,092752 / 1,271663 | 213 |
| 100 RPS, 30 s | 3.000 | 2.920 | 80 | 0,678522 / 0,899202 / 1,271663 | 2.920 |

Geração medida: 30,0039188 s e 30,0061302 s. Maior RSS **amostrado nas janelas**: 69.177.344 e 80.416.768 bytes; CPU acumulada do processo na última janela: 1.329,451 e 3.578,207 ms. São medições operacionais, sem plateau demonstrado ou overhead isolado. A dispersão frente aos primeiros probes confirma que não há envelope estável calibrado neste ambiente. Hashes e metadados completos nos `*.summary.json`; buckets, incerteza e populações nos `*.report.json`. Essas amostras não medem sockets/alocações/pausas GC.

## Gates abertos em 2026-10-17 (histórico)

- Cenário de 30 s sem saturação: **não comprovado**; os probes exibem drops e não passam no requisito de zero drops, mesmo quando percentis são baixos. Investigar lateness/ambiente sem reduzir o critério retrospectivamente.
- Warm-up identificado, pelo menos três repetições estáveis e dispersão, CPU restrita e medições de dispatch/sockets/alocações/GC/contenção.
- Ensaios de 2 e 10 minutos para plateau de recursos e capacidade/envelope condicionado ao host; código limitado e testes de fila/amostras não substituem essa medição.
- Vetores restantes da 003 e revisão de recursos; gate completo de todas as fichas anteriores.
- Sem validação de produção remota, NAT/egress/IP distribuído, cofre ou UI/SSE nesta etapa.

Naquela entrega os três critérios de pronto da 012 permaneceram abertos e M1/FICHA-037 não foram concluídos. Não extrapolar as medições daquele WSL para capacidade do alvo ou ambiente externo. O aceite atual de M1 está no fechamento abaixo; FICHA-037 continua parcial nos marcos posteriores.

## Continuação — calibração reproduzível e recursos (2026-10-21)

Transporte ampliado na [003](FICHA-003-HTTP-TRANSPORT.md); espera Windows de alta resolução e diagnóstico na [005](FICHA-005-ARRIVAL-SCHEDULER.md); métricas GC/ThreadPool/HTTP agregadas na [010](FICHA-010-BOUNDED-OBSERVABILITY.md). O critério de zero drops e tolerância de 5 ms não foram relaxados. Validação integrada Linux após a instrumentação: **571 aprovados, zero falhas/ignorados**, build Release sem avisos/erros, em `TestResults/M1/LinuxFinal/`. Rodada Windows anterior à integração do listener HTTP: **565 aprovados**, em `TestResults/M1/WindowsSuite/`.

O utilitário versionado agora preserva rodadas em diretórios UTC, separa warmup e medições, executa repetições sequenciais e durações progressivas e produz `manifest.json`, `summary.json`, configuração/relatório/logs/sumário/série de recursos por cenário. Manifesto inclui runtime/SO/arquitetura/CPU, frequência do relógio, SHA-256 dos binários do motor/CLI e dos planos sanitizados nos sumários. `--output` não reutiliza diretório com manifesto. `--durations` aceita 1–600 s, `--rps` 1–1.000; nenhuma opção aumenta limites da CLI.

```sh
dotnet build tools/Dinamico.LocalValidation -c Release
dotnet tools/Dinamico.LocalValidation/bin/Release/net10.0/Dinamico.LocalValidation.dll . --quick
dotnet tools/Dinamico.LocalValidation/bin/Release/net10.0/Dinamico.LocalValidation.dll . --calibrate --require-gates
# Perfil direcionado; mantém 3x30 s e ensaios de 120/600 s:
dotnet tools/Dinamico.LocalValidation/bin/Release/net10.0/Dinamico.LocalValidation.dll . --calibrate --rps 10 --require-gates
```

No WSL com runtime Windows, usar `dotnet.exe`; no Linux desta sessão, `DOTNET_HOST_PATH=/tmp/dinamico-sdk/dotnet` identifica o host dos subprocessos. Sem `--require-gates`, exit 0 exige somente conciliação funcional; com a opção, falha no gate de capacidade também retorna 1. Timeout encerra a árvore filha. Ctrl+C interrompe o utilitário com 130 e encerra o gerador ativo; evidências por cenário concluído permanecem disponíveis, sem sumário final de execução interrompida.

Warmup roda em processo separado: aquece o alvo e caches do SO, mas não o JIT/pool do processo medido. Essa limitação aparece no manifesto; não há tráfego oculto na CLI. A conciliação exige processo/dados completos, cobertura/flush, identidades de contadores, 202 completo para todos os inícios, saldo final zero e recebimentos da fixture. Gate de capacidade é avaliado separadamente, exclui warmup e exige zero drops, qualidade sem flags e RPS iniciado dentro de ±2%. Dispersão é calculada por taxa/duração, com média, extremos, desvio amostral e coeficiente de variação. Séries de recursos e crescimento descritivo da segunda metade não certificam plateau automaticamente.

Baseline Windows antes da correção: 87/300 a 10 RPS e 962/3.000 a 100 RPS, respectivamente 213 e 2.038 drops de atraso. Smoke após a correção: 300/300 (zero drops) e 2.999/3.000 (um drop), com conciliação completa. Artefatos do segundo smoke em `TestResults/M1/WindowsHighResolution/`. Runtime Windows medido: .NET 10.0.13, Windows 10.0.26300, x64, 16 processadores lógicos, SDK 10.0.204. São medições locais, não limites universais.

O diagnóstico Linux curto de 5 s (`TestResults/M1/LinuxQuick/`) conciliou dados, mas não passou capacidade: 43/50 e 453/500 inícios. Nenhuma equivalência de capacidade entre Windows e WSL foi presumida.

### Resultado Windows — perfil de 10 RPS

Comando: `dotnet.exe tools/Dinamico.LocalValidation/bin/Release/net10.0/Dinamico.LocalValidation.dll . --calibrate --rps 10 --require-gates --output TestResults/M1/WindowsCalibration`. Retorno **0**, `functionalPassed=true`, `nonSaturatedGatePassed=true` para todas as medições. Warmup separado de 100 RPS/10 s conciliou 999/1.000, com um drop; não entra nos gates medidos ou na dispersão. Nenhum ensaio foi repetido para substituir resultado reprovado.

| Perfil | Iniciado = recebido = completo = sucesso | Drops | RPS iniciado | P95 individual (ms) | Pico RSS amostrado (MiB) |
|---|---:|---:|---:|---:|---:|
| 30 s, repetição 1 | 300/300 | 0 | 9,999657 | 0,439967 | 50,39 |
| 30 s, repetição 2 | 300/300 | 0 | 9,999754 | 0,412286 | 50,65 |
| 30 s, repetição 3 | 300/300 | 0 | 9,999650 | 0,430539 | 50,60 |
| 120 s | 1.200/1.200 | 0 | 9,999925 | 0,317916 | 56,17 |
| 600 s | 6.000/6.000 | 0 | 9,999977 | 0,285284 | 66,07 |

Os percentis são limites superiores aproximados de cada população individual, não um percentil agregado das execuções. O utilitário calcula dispersão entre P95s individuais; essa média não representa P95 consolidado. RPS das três repetições: coeficiente de variação **0,000583%**. Latência é do cliente, com gerador e alvo no mesmo host.

Em 600 s: 121 amostras, atraso máximo no poll **3,3803 ms**, pico de uma conexão, zero conexões ao final. RSS máximo 69.283.840 bytes, final 66.281.472 bytes, variação da segunda metade **−1,29%**; handles entre 280–395, final 330, variação da segunda metade **−1,79%**. GC: duas coletas Gen0, uma Gen1, zero Gen2, pico de heap aproximado válido 17.695.288 bytes e 41.690.824 bytes alocados cumulativamente. CPU entre primeira/última amostra: **0,213% de um core**; não inclui startup antes da primeira janela. Essas observações apoiam retenção limitada neste perfil, sem estimar capacidade máxima.

Uma amostra de `GC.GetTotalMemory(false)` retornou −156.944 bytes durante a rodada; é inválida e não foi usada como memória retida. A publicação passou a produzir null para leitura negativa. Após a rodada também foi corrigido o encerramento da observabilidade em falha de criação/publicação da saída, com teste de duas invocações da CLI no mesmo processo. Esses ajustes não mudam agenda, transporte, limites ou as identidades medidas; os hashes dos binários anteriores estão no manifesto da calibração. Uma rodada posterior dos binários finais identifica seus próprios hashes.

### Cenário limitado e confirmação final

```sh
DOTNET_HOST_PATH=/tmp/dinamico-sdk/dotnet taskset -c 0 /tmp/dinamico-sdk/dotnet tools/Dinamico.LocalValidation/bin/Release/net10.0/Dinamico.LocalValidation.dll . --quick --rps 1000 --durations 120 --repetitions 1 --require-gates --output TestResults/M1/LinuxRestricted
```

Afinidade de um core herdada pelo gerador e fixture; não é quota de CPU e não isola os dois processos. Runtime Linux .NET 10.0.8, x64, `processorCount=1`. Planejado 120.000, iniciado/recebido/completo/sucesso **58.502**, descartes **61.498** (2.259 tolerância, 59.235 teto por despertar, quatro no fim). `dataComplete=true`, `GeneratorLimited`, `functionalPassed=true`, capacidade false. A CLI retorna 0 por dados completos; o utilitário com `--require-gates` retorna **1**, corretamente distinguindo as duas avaliações. O resultado não demonstra saturação de CPU: o gerador usou 18,72% de um core entre amostras; não foi identificada causa exclusiva dos atrasos.

Em 25 amostras, RSS máximo 86.081.536 bytes (82,09 MiB), crescimento da segunda metade 1,04%; heap aproximado máximo 17.785.056 bytes, final 4.839.480; 237.726.320 bytes alocados, 14 coletas Gen0, duas Gen1 e uma Gen2. Handles entre 94–102, final 94; conexões pico oito, final zero. Nenhuma métrica esperada ficou ausente nessa rodada. Contadores, recursos limitados e flag sob limitação foram comprovados sem exigir 1.000 RPS neste host.

Confirmação dos binários finais, após cleanup/guarda do heap: `dotnet.exe tools/Dinamico.LocalValidation/bin/Release/net10.0/Dinamico.LocalValidation.dll . --quick --rps 10 --durations 30 --repetitions 1 --require-gates --output TestResults/M1/WindowsFinalSmoke`. **300/300**, zero drops, dados completos, ambas as flags true, retorno **0**; hashes próprios em `manifest.json`. Warmup separado de dois segundos: 199/200, um drop, fora da avaliação de capacidade medida.

Validação final: **573 aprovados, zero falhas/ignorados por plataforma**, em `TestResults/M1/WindowsFinal/welli_WELL-PC_2026-10-21_21_36_08.trx` e `TestResults/M1/LinuxFinalCleanup/_Well-PC_2026-10-21_21_36_46.trx`; Release sem avisos/erros, restore locked-mode e `git diff --check` aprovados. O lock de testes ganhou somente a referência ao projeto CLI, necessária para verificar liberação de assinaturas entre invocações no mesmo processo; nenhum pacote externo foi acrescentado. A rodada Linux intermediária teve uma falha no novo teste que exigia atraso mínimo da espera individual. O contrato já admite retorno antecipado da espera; o teste foi corrigido para verificar que a agenda real nunca oferece antes do due e mantém as identidades. Treze direcionados finais passaram antes da repetição das suítes.

### Aceite e limites

**M1 aceito no escopo local existente.** Há cenário sem saturação repetido e prolongado, cenário limitado com flags explícitas, conciliação independente com a fixture, retenção limitada observada e correções de recursos verificadas. Envelope demonstrado: **10 RPS, GET/202, corpo de 16 bytes, C=32, timeout 2 s, até 600 s no Windows/host identificado**. A faixa configurável até 1.000 RPS não é uma capacidade prometida; perfis com outros tamanhos, concorrência, TLS ou ambientes precisam de nova medição. WSL funcional permanece limitado nos perfis ensaiados. A amostragem não equivale a profiling exaustivo; sockets do kernel, lock contention, quotas/throttling não foram medidos diretamente. Nenhuma evidência distribuída, segurança remota ou produção foi inferida.

Fichas 001–012 encerram o primeiro marco. A próxima etapa é detalhar 013 (persistência PostgreSQL), sem antecipar Control Plane/Kubernetes/UI. A FICHA-037 conclui apenas sua etapa M1; M2–M6 permanecem futuros.
