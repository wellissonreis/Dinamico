# Entrega de Rate Limiting — 2026-10-12

Estado atual (2026-10-17): observabilidade 010 e CLI/relatório 011 implementados; calibração/gate 012 e transporte remanescente pendentes. M1/037 continuam parciais. Ver [012](implementation-cards/FICHA-012-ENGINE-VALIDATION.md) e [README atual](../README.md). O corpo desta entrega e suas continuações datadas são registros históricos; afirmações sobre host informativo ou Git inexistente descrevem aquelas datas.

## Estágio e escopo original

Inspeção de README, documentos 01–08, REVIEW, ADRs 001–010, índice/fichas 001–012/037, projetos, código e testes: somente FICHA-001 implementada. Host local informativo; não existem transporte, fixture, scheduler, agregador, CLI funcional, banco, Control Plane, Kubernetes ou React. Não foi encontrado AGENTS.md aplicável. `git status` não reconhece o diretório reservado `.git`; não há baseline Git/commit/PR nesta entrega. Foi preservada cópia anterior em `/tmp/dinamico-before-rate.tgz` para revisão por comparação de arquivos.

A autorização atual permite integrar Rate Limiting aos componentes existentes, respeitando dependências. Resultado: extensão da FICHA-001 e **FICHA-037 parcialmente implementada em M1**. Nenhuma ficha 002–012 ou marco futuro foi marcado concluído. O registro histórico de 119 testes da base não é evidência desta execução.

## Decisões

- Plano schema 2; enums explícitos de modo, retry None e regra Completed2xxV1. Valores desconhecidos são rejeitados. A regra exige corpo completo e status 200–299; 202 não comprova processamento de negócio. Nenhum retry de carga habilitável.
- Normal/Authorized/Diagnostic preservam perfil, limites e destino. Authorized requer declaração e índice de um header de teste não vazio, no mesmo snapshot imutável; não constitui grant nem prova validade da credencial. API keys/Bearer/headers personalizados reutilizam a validação existente, sem overwrite, header automático ou bypass.
- Status final 200–599; 429 deriva do status mesmo se houver falha posterior de corpo. Não adicionar um desfecho terminal concorrente de Rate Limiting. Outros erros HTTP não são automaticamente limitadores.
- Retry-After é evidência finita sem texto bruto: limite de 128 caracteres e duração normalizada de 86.400 s, com clamp explícito. Parsing puro de segundos/datas, usando referência UTC fornecida, sem I/O, relógio implícito ou espera. `NotObserved` distingue captura não realizada de `Missing`; datas têm incerteza explícita. A semântica de datas/segundos segue a [RFC 9110](https://www.rfc-editor.org/rfc/rfc9110.html#section-10.2.3), incluindo [formatos HTTP e anos de dois dígitos](https://www.rfc-editor.org/rfc/rfc9110.html#section-5.6.7).
- Sem alteração dos ADRs open-loop, histograma, comunicação, segurança remota ou falhas. Documentado antes da implementação o recorte local de credenciais e schema. HTTP loopback com credenciais sintéticas já era aceito; a exceção foi explicitada em 07. HTTPS/cofre/grants continuam gates remotos.
- Contratos não são agregadores. Status deverá ser registrado na janela de headers sem recontagem no terminal. Retry-After terá contagens fixas/min/max, merge idempotente e cobertura nas fichas responsáveis. Nenhum serviço novo.

## Coordenação exclusivamente por tmux

CLI inspecionada com `codex --help` e `codex exec --help`; modelos/esforços conferidos no catálogo local `models_cache.json`. Sintaxe usada: `codex exec --ephemeral --skip-git-repo-check --ignore-user-config -C /mnt/c/Repos/Dinamico -s <sandbox> -m <model> -c model_reasoning_effort=<effort> '<instruções>'`.

O socket tmux foi bloqueado inicialmente pelo sandbox; após aprovação de acesso ao socket, foi criada a sessão `rate` no servidor `tmux -L dinamico-rate`. Instruções e acompanhamentos usaram `send-keys` e `capture-pane`; nenhum mecanismo de subagentes/plugins foi usado. Não foram criados worktrees. Agentes de execução usam workspace-write ou read-only e não receberam autorização para delegar ou executar testes.

| Responsável / janela | Modelo e esforço | Ownership |
|---|---|---|
| Coordenador, sessão principal | Modelo da sessão informado pelo usuário como Astra 6; não alterado nem aferido pelo CLI de agentes | Contratos de configuração/plano/observação, validação, integração, testes dos contratos, documentos canônicos 01/03/04/06/07, revisão final e verificações |
| `rate:0` — arquitetura/segurança | `gpt-6-astra`, `high`, confirmado no cabeçalho de execução | Somente leitura; revisão semântica inicial e revisão de código |
| `rate:retry-after` — evidência | `gpt-6.1-sol`, `high`, confirmado no cabeçalho de execução | Somente RetryAfterObservation.cs e RetryAfterObservationTests.cs; sem transporte/agregação |
| `rate:docs` — integração documental | `gpt-6.1-sol`, `medium`, confirmado no cabeçalho de execução | README principal, docs/README, REVIEW, roadmap e fichas; sem código |

A revisão inicial recomendou `NotObserved` para evitar ausência fictícia de header; a decisão foi incorporada. A revisão dos contratos de configuração/observação não encontrou bloqueantes, sob as limitações explicitadas. Revisões dos agentes são estáticas; execução e registro dos testes pertencem ao coordenador.

## Verificações

Evidência recuperada na continuação: `TestResults/RateLimiting/rate-limiting.trx` registra 309 testes executados/aprovados, zero falhas. Os documentos da entrega anterior registram build Release sem avisos/erros.

## Pendências e limites da evidência

- 002/003: fixture, captura efetiva de 429/Retry-After, streaming, segurança do transporte e comparação com recebimentos do servidor.
- 004–009: tempo, agenda, admissão, histogramas, janelas e ciclo de vida. A imutabilidade dos contratos é testável agora; invariância operacional do open-loop sob 429 ainda depende desses componentes.
- 008/010/011/012: agregação finita, cobertura, denominadores, relatório e canários nos canais reais. Não existem logs, relatórios de carga ou SSE a exercitar nesta etapa. `ToString()` e erros seguros não tornam segura a serialização arbitrária de configuração/plano completo.
- M2/M3: persistência idempotente, referências versionadas de credenciais, cofre, revogação e autenticação/autorização REST.
- M4/M5: Jobs, inventário de egress com origem/validade, NAT compartilhado, merge e falhas parciais. Nenhuma evidência de IP público foi produzida; não inferir IP de saída a partir de Pod/nó.
- M6: configuração, acompanhamento SSE e relatório React. Nenhuma tela foi criada.

Próxima integração: implementar fixture 002 e transporte 003 com os casos da 037; seguir os gates das fichas até concluir M1. 429 sinaliza limitação, mas não identifica o componente; não inferir capacidade real enquanto houver interferência nem interpretar envio como processamento pelo servidor.


## Continuação — fixture local (2026-10-12)

Estado recuperado antes de alterações: Git acessível fora do sandbox, branch `main` sem commits, arquivos adicionados ao índice; alteração preexistente do lockfile apenas de finais de linha, preservada. A sessão tmux anterior `dinamico-rate` e o backup temporário anterior não existem nesta sessão. O painel padrão `0:0.0` pertence ao coordenador, não a executor reutilizável. Último ponto comprovado: TRX com 309 testes aprovados.

Próxima dependência selecionada: FICHA-002, fixture HTTP real em loopback. Esta continuação autoriza avançar além do recorte puro anterior; transporte de produção, agenda, agregador e marcos remotos continuam nas fichas responsáveis. Cada cenário usa nova fixture, evitando reset concorrente. Contadores de recebimentos e conexões são independentes dos desfechos do cliente. Não representam métricas do motor.

Execução exclusivamente via tmux, CLI 0.162.1 e catálogo local conferidos, sem worktrees. Servidor `dinamico-rate`, sessão `rate`, mouse habilitado. Cabeçalhos confirmam ambos os executores `gpt-6.1-sol`, reasoning `high`:

| Painel | PID executor / painel | Ownership |
|---|---|---|
| `rate:0.0` (`fixture`) | 2967 / 2945 | `Fixtures/ControlledHttpFixture.cs` e csproj de testes |
| `rate:1.0` (`tests`) | 2999 / 2958 | `HttpFixtureTests.cs` |

Coordenador: Astra 6, conforme identificação do painel principal; contratos, revisão, integração, execução dos testes e documentação. Nenhum executor altera código de produção. Os relatórios e logs temporários estão em `/tmp/dinamico-rate-next/`.


Resultado integrado: fixture 002 concluída, FICHA-037 ainda M1 parcial. Arquivos de código: `tests/Dinamico.LoadEngine.Tests/Fixtures/ControlledHttpFixture.cs`, `tests/Dinamico.LoadEngine.Tests/HttpFixtureTests.cs`, csproj de testes. Atualizados somente os registros/status em README, docs/README, REVIEW, roadmap, índice de fichas e fichas 002/037, além deste registro.

Verificação nesta continuação:

- `dotnet.exe restore Dinamico.slnx --locked-mode`: aprovado, sem novos pacotes.
- `dotnet.exe build Dinamico.slnx --configuration Release --no-restore`: aprovado com analisadores habilitados, zero avisos/erros.
- `dotnet.exe test Dinamico.slnx --configuration Release --no-build --no-restore --logger 'trx;LogFileName=fixture-continuation.trx' --results-directory TestResults/FixtureContinuation`: **365 aprovados**, zero falhas/ignorados; 56 casos novos. SDK 10.0.204, .NET/ASP.NET 10.0.12, Windows x64 via WSL.
- Primeira compilação apontou quatro Task.Result rejeitados pelo xUnit1031, corrigidos para await. Execução diagnóstica intermediária passou, mas o teste seguinte revelou corrida real de TCP reset antes da captura dos headers. Corrigida com ReleaseTruncation após captura, espera finita de 10 s e cancelamento no dispose. O resultado final acima usa build normal e a correção integrada.
- Prova de socket liberado reforçada: conexão recusada e rebind, evitando falso positivo de reutilização de endereço no Windows. Falhas intermitentes da ponte WSL dentro do sandbox exigiram executar os comandos .NET fora dele; não são falhas do produto.

Logs CLI informaram “tokens used”: fixture 47.270; testes 34.248. Não forneceram divisão entrada/cache/saída; esses valores são o agregado reportado pela CLI, sem inferir categorias ou consumo do coordenador. Executores terminaram; painéis tmux permanecem navegáveis para consulta, sem agentes ociosos executando.

Próximo passo: transporte 003, usando esta fixture para captura efetiva de status/Retry-After e preservação de status em falhas de corpo. Não antecipar scheduler/agregação distribuída. Ausência de retry do motor e invariância open-loop continuam sem evidência operacional nesta etapa. A fixture no mesmo host não mede capacidade de produção.

Na conferência final, o histórico passou a conter `59a7858 Initial commit` e o Git estava limpo, incluindo os novos arquivos da fixture. Esse commit surgiu externamente durante o trabalho; o coordenador não executou commit nem alterou o índice. Ajustes finais de status/evidência posteriores permanecem no working tree.

## Continuação — transporte HTTP 003 parcial (2026-10-12)

Verificação inicial desta sessão: branch `main`; alterações já existentes em README, FICHA-037 e neste registro foram preservadas. A sessão tmux anexada foi reutilizada e renomeada para `dinamico-m1`, com o painel 0 de coordenação, painel 1 de testes e painel 2 para o mapa da suíte. Workers read-only concluídos: PIDs 9366 (`gpt-6.1-sol`, medium; revisão dos contratos de 003) e 9367 (`gpt-6.1-sol`, low; inventário de testes). Sem worktrees e sem commits.

Implementado `LocalHttpTransport` reutilizável por plano validado: HTTP/1.1 exato; conexão presa ao IP loopback; TLS com identidade da URI preservada; proxy, redirect, cookies e decompression automática desativados; pool/conexões/headers limitados; `ResponseHeadersRead`, buffer devolvido ao ArrayPool e contagem de bytes até o teto configurado. Cada chamada possui request/content/response próprios e recebe o token de cancelamento até conexão/envio/leitura. Status e Retry-After são capturados antes do corpo, sem reter texto bruto, e permanecem na observação quando o corpo falha. Não há timeout total por request neste incremento; a classificação de deadline depende da 004. A fixture agora oferece corpo sem Content-Length e soma bytes de request recebidos, sem guardar requests.

Testes integrados com a fixture cobrem payload/reuso de conexão, 429 + Retry-After antes de truncamento, Missing/NotApplicable, streaming sem Content-Length, cancelamento após headers, redirect sem seguimento, HTTP 200/202 e HEAD. A suíte completa passou após corrigir uma expectativa de teste que somava o payload das duas chamadas:

- `dotnet.exe test Dinamico.slnx --configuration Release --no-restore --logger 'console;verbosity=minimal'`: **374 aprovados**, zero falhas/ignorados; build Release bem-sucedido. Log: `/tmp/dinamico-m1-final-tests.log`.
- Reexecução sem build com logger TRX: `TestResults/M1Transport/m1-transport.trx`, 374 aprovados, zero falhas/ignorados.

FICHA-003 permanece parcial: ainda faltam deadline/timeout 004, captura de outros vetores HTTP pelo transporte, integração à agenda/admissão, métricas/janelas, ciclo local, observabilidade, CLI e gate 012. Em particular, não há evidência de invariância open-loop, agregação de status uma única vez, canários em logs/relatórios ou calibração de recursos. FICHA-037 permanece M1 parcial. Próxima tarefa recomendada: implementar a 004 em dependência direta deste transporte e depois integrar 005/006; preservar a fixture e comparar recebimentos com operações iniciadas/concluídas.

## Retomada do trabalho pendente — 2026-10-13

O working tree já continha `Timing/`, `Scheduling/ArrivalSchedule.cs`, `ArrivalScheduler.cs`, `ISchedulerWait.cs`, `TimingDeadlineTests.cs` e `ArrivalSchedulerTests.cs`, sem evidência atualizada nas fichas. Não foi possível atribuir a interrupção a um limite específico de tokens; o estado real comprova implementação ainda não compilável/documentada. Preservadas as alterações preexistentes. A primeira suíte revelou CS9124 no FakeClock dos testes da agenda: o parâmetro primário era capturado e também usado no inicializador de campo. Corrigido usando a propriedade Origin para guardar a origem.

Deadline 004 e agenda 005 agora compilam e passam testes. O deadline integrado cobre envio/leitura e preserva status/Retry-After em timeout/cancelamento; os testes determinísticos verificam corridas, timers e UTC. Agenda tem aritmética racional, microbateladas limitadas, saltos O(1), relógio/espera substituíveis e partição matemática futura. A invariância dos modos foi testada com respostas fictícias; ainda falta o gate operacional completo com os três modos e janelas reais.

Implementada FICHA-006 em `Scheduling/BoundedAdmission.cs`: aquisição imediata sem fila, no máximo C operações, callback terminal, started/completed/in-flight/pico, recusas concurrency_limit/policy, fechamento de admissão e drain. Não retém tarefas concluídas e não usa Task.Run por operação. Exceções inesperadas do transporte viram InternalFailed; falha do consumidor fecha admissão, libera slot e é reportada ao host pelo drain. Cancelar a espera de drain não cancela automaticamente as operações; o host deve passar seu token ao iniciar operações.

`BoundedAdmissionTests.cs` cobre saturação/liberação, concorrência entre produtores, falhas síncronas/assíncronas, cancelamento, falha do consumidor e 100.000 operações. Teste integrado com agenda simulada e HTTP real lento concilia 100 chegadas, 2 inícios, 98 recusas por concorrência, 2 recebimentos na fixture e 2 terminais 429 completos. Não há retry/adaptação por Retry-After. A fixture no mesmo host e o relógio simulado não medem capacidade ou precisão temporal real do gerador.

Execução em tmux: sessão existente `0` preservada com seu painel `0:0.0`; criada janela `0:1` (`validacao`) para testes/build/Git. Nenhum processo do usuário encerrado, nenhuma sessão renomeada, nenhum commit ou worktree criado.

Validação final: `dotnet.exe test Dinamico.slnx --configuration Release --no-restore --logger "trx;LogFileName=continuacao.trx" --results-directory TestResults/Continuacao`: **455 aprovados**, zero falhas/ignorados. TRX: `TestResults/Continuacao/continuacao.trx`; log `/tmp/dinamico-continuacao-final.log`. Build Release sem restore e `git diff --check` executados na mesma janela tmux; logs `/tmp/dinamico-continuacao-build.log` e `/tmp/dinamico-continuacao-diff.log`.

Próximas dependências pendentes: 007 histograma/codec, 008 janelas/contadores e Retry-After agregado, 009 ciclo local, 010 observabilidade, 011 CLI/relatório e 012 calibração/aceite. Transporte 003 ainda parcial quanto aos vetores restantes; M1 e FICHA-037 não estão concluídos. Marcos remotos permanecem futuros.


## Continuação atual — FICHA-007 (2026-10-17)

Histograma `log2-32-v1` e codec implementados em `src/Dinamico.LoadEngine/Metrics/`, sem pacote adicional. Tabela canônica de 833 limites exatos e 834 contadores UInt64 com overflow; registro, N/soma/min/max, P50/P95/P99 por rank inteiro e merge puro. Codec v1 little-endian de 6.760 bytes com schema, checksum SHA-256 e validação estrutural/semântica; detalhes e comandos completos na [FICHA-007](implementation-cards/FICHA-007-HISTOGRAM.md#entrega-e-validação--2026-10-17). Contrato 03 e ADR-005 atualizados com as decisões de representação; nenhuma janela/consumidor antecipado.

Validação desta entrega: restore locked (sem fallback inexistente do Visual Studio), 17 testes direcionados aprovados, build Release com zero avisos/erros, suíte completa com **472 aprovados**, zero falhas/ignorados; `git diff --check` aprovado. TRX: `TestResults/Ficha007/ValidatedDirected/welli_WELL-PC_2026-10-17_17_25_34.trx` e `TestResults/Ficha007/ValidatedSuite/welli_WELL-PC_2026-10-17_17_25_38.trx`. Lockfile inalterado. A tentativa anterior falhou em build por MSB4018/fallback inválido; a suíte de 469 com binário anterior não vale como validação final. Os 455 testes em `TestResults/Continuacao/continuacao.trx` são evidência histórica de 13/10, preservada acima.

Próximo passo: 008 janelas/contadores, expondo evento de headers do transporte separado do término da operação para status e Retry-After na janela correta. Transporte 003 parcial, CLI informativa; 008–012, M1 e FICHA-037 ainda incompletos. Não houve implementação remota, refatoração de componentes anteriores, dependência nova ou commit.


## Continuação atual — FICHA-008 (2026-10-17)

Contadores/janelas locais implementados: registro serializado de inícios, drops, headers e terminais, snapshots imutáveis de 5 s, vazias/drain/parcial, manifesto de cobertura, dois histogramas, consolidação contígua com último gauge e sink limitado a seis janelas/240 KiB de reserva calculada. Callback explícito de headers do transporte precede o corpo e mantém status/Retry-After separados do terminal. Codec/schema de janela 1, little-endian, 17.832 bytes, SHA-256 e validação estrutural/semântica. Decisões, layout, arquivos, comandos e pendências na [FICHA-008](implementation-cards/FICHA-008-METRIC-WINDOWS.md).

Validação: restore locked sem alteração do lockfile; **24 casos novos, 87 direcionados e 496/496 na suíte completa**, zero falhas/ignorados; build Release zero avisos/erros; `git diff --check` aprovado. TRX: `TestResults/Ficha008/ValidatedDirected/welli_WELL-PC_2026-10-17_17_39_09.trx` e `TestResults/Ficha008/ValidatedSuite/welli_WELL-PC_2026-10-17_17_39_18.trx`. A primeira rodada teve uma asserção incorreta do subtipo de exceção (84 aprovados/1 falha), corrigida; resultados finais recompilados acima. Os 472 testes da 007 e 455 de 13/10 permanecem históricos.

Seis janelas tmux temporárias ociosas das validações 007/008 foram fechadas após conferir shell sem processos filhos; processos e sessões do usuário preservados. Nenhuma ficha futura foi cancelada por ainda não estar em uso: são dependências do plano. Próxima ficha é 009 (controlador, geração/drain/flush); 009–012 continuam pendentes, CLI informativa, transporte 003 parcial e M1/FICHA-037 incompletos. Sem commit, SQL/CP/Kubernetes/UI, benchmark de contenção ou medição de RSS nesta continuação.


## Continuação atual — FICHA-009 (2026-10-17)

API local `LocalRunController.RunAsync(plan, output, token)` integra agenda, admissão, transporte, headers separados do terminal, janelas e saída confirmada. Parada/cancelamento impedem novos inícios; fim natural drena I/O sem cancelá-lo. Due conciliado no instante latched da parada; futuras chegadas são not_scheduled_after_stop. Sink retém até seis janelas sem ACK, inclusive a que está sendo gravada. Flush tem deadline de 10 s e grace de cancelamento de 1 s. Resultado separa razão, falhas, qualidade, cobertura, operações drenadas e saída/flush confirmados; não contém plano sensível nem exceção bruta. Adaptadores internos não cooperativos são marcados incompletos, com observação tardia e dispose adiado, sem sucesso silencioso.

Validação final: restore locked, lockfile inalterado; **19 casos novos, 97 direcionados e 515/515 na suíte completa**, zero falhas/ignorados; Release zero avisos/erros; `git diff --check` aprovado. TRX: `TestResults/Ficha009/ValidatedDirected/welli_WELL-PC_2026-10-17_18_14_45.trx` e `TestResults/Ficha009/ValidatedSuite/welli_WELL-PC_2026-10-17_18_14_54.trx`. Registros dos comandos, ajustes de compilação dos testes e rodada ampliada com uma corrida reproduzida/corrigida estão na [FICHA-009](implementation-cards/FICHA-009-LOCAL-LIFECYCLE.md). Repetição focada EOF/cancelamento e writer bloqueado: dez execuções, **20/20 casos aprovados** em `TestResults/Ficha009/Races/`, incluindo cem corridas internas de EOF. Testes específicos de riscos/bugs; nenhum CRUD trivial. Evidência anterior: 496 na 008, preservada acima.

Commits autorizados pelo usuário durante esta tarefa: pré-requisitos separados por ficha/responsabilidade (`d665845`, `c1630a2`, `e9f87c4`, `a9e7c60`, `8b14fe4`, `4af40eb`); contratos/handoff da 009 (`c8ab487`) e controlador/regressões (`7290db7`) e proteção do início do flush (`c3c1593`). Configurações locais do agente não incluídas; alterações existentes preservadas. A afirmação histórica de “nenhum commit” nas entregas anteriores vale para aquelas etapas, antes da autorização posterior.

Próxima ficha: 010 — observabilidade limitada; CLI/relatório 011 e gate 012 continuam pendentes. A mensagem informativa da CLI foi corrigida para refletir a API existente, sem implementar a CLI. SIGTERM em subprocesso, CPU/RSS/lock e calibração do host não executados nesta ficha. Transporte 003 parcial, M1/FICHA-037 incompletos; sem SQL, CP, Kubernetes ou React.
