# FICHA-012 — Calibração e aceite do primeiro marco

Prioridade: P0. Complexidade: M. Marco: M1. Status: validação funcional parcial executada em 2026-10-17; calibração e aceite M1 pendentes.

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

- [ ] Evidências e parâmetros reproduzíveis registrados.
- [ ] Correções necessárias de M1 concluídas e verificadas.
- [ ] Envelope medido e limitações revisados antes de detalhar M2.

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

## Gates ainda abertos

- Cenário de 30 s sem saturação: **não comprovado**; os probes exibem drops e não passam no requisito de zero drops, mesmo quando percentis são baixos. Investigar lateness/ambiente sem reduzir o critério retrospectivamente.
- Warm-up identificado, pelo menos três repetições estáveis e dispersão, CPU restrita e medições de dispatch/sockets/alocações/GC/contenção.
- Ensaios de 2 e 10 minutos para plateau de recursos e capacidade/envelope condicionado ao host; código limitado e testes de fila/amostras não substituem essa medição.
- Vetores restantes da 003 e revisão de recursos; gate completo de todas as fichas anteriores.
- Sem validação de produção remota, NAT/egress/IP distribuído, cofre ou UI/SSE nesta etapa.

Os três critérios de pronto da 012 permanecem abertos. **M1 e FICHA-037 não concluídos.** Não extrapolar as medições deste WSL para capacidade do alvo ou ambiente externo.
