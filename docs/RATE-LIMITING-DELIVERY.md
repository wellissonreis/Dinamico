# Entrega de Rate Limiting — 2026-10-12

## Estágio e escopo

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
