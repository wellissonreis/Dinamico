# FICHA-038 — Investigar requisições ausentes ao elevar a carga

Origem: ficha 1 relatada pelo usuário em 2026-10-25. Prioridade: P0. Status: diagnóstico implementado e validado no escopo local; reprodução da execução original pendente dos parâmetros/resultado daquele teste.

## Entrega atual — 2026-10-25

Funil, descartes por motivo, desfechos, status, cobertura e diagnóstico do scheduler disponíveis ao vivo, no histórico e no download. Testes determinísticos reproduzem descarte de metade por teto de despacho e por concorrência; ensaios HTTP reais crescentes conciliam todos os inícios com recebimento independente da fixture. Nenhuma perda silenciosa foi demonstrada nesses ensaios. A taxa de 1.000 req/s apresentou descartes do scheduler e não passou o gate de capacidade, mesmo com dados completos. Não atribuir esse resultado automaticamente ao teste original nem afirmar correção de um bug ainda não comprovado.

Implementação em `src/Dinamico.Web/{Views,ExecutionService}.cs`, `FrontEnd/src/{RunChart,RunDiagnostics,RunResult}.tsx`, coleta em `src/Dinamico.LoadEngine/Observability` e testes do motor/web. [Resultados, custo medido e comandos](../DIAGNOSTICS-DELIVERY.md); [como usar](../WEB-LOCAL.md#diagnóstico-de-requisições-e-recursos). As seções seguintes preservam o plano e as evidências de abertura anteriores à implementação.

Aceite local: 630 testes .NET e seis testes Chromium aprovados, builds Release/frontend aprovados. A investigação do relato original não é declarada encerrada apenas por reproduzir uma perda semelhante em cenário controlado.

## Objetivo

Explicar com evidências por que aproximadamente metade das requisições deixa de aparecer quando a taxa aumenta, identificar eventuais bugs e distinguir limitação do gerador, falha de transporte, resposta do alvo e perda de métricas. O aceite exige conclusão sobre o cenário reproduzido, além da melhoria de apresentação.

## Evidências iniciais e limites

- `Scheduling/BoundedAdmission.cs` recusa chegadas quando atinge `MaxConcurrency`, sem fila pendente. `Scheduling/ArrivalSchedule.cs` e `ArrivalScheduler.cs` limitam despacho e atraso em uma agenda open-loop. Esses mecanismos podem produzir requisições não iniciadas; sua existência não comprova a causa do relato.
- `Metrics/MetricWindow.cs` mantém `SchedulerLate`, `ConcurrencyLimit` e `Policy` separados, além de desfechos terminais. `src/Dinamico.Web/Views.cs` soma as três causas em `Dropped`; a interface `FrontEnd/src/RunResult.tsx` mostra apenas o agregado. Há informação existente que não chega detalhada ao usuário.
- A [012](FICHA-012-ENGINE-VALIDATION.md) registra um ensaio anterior Linux restrito a 1.000 req/s por 120 s: 120.000 planejadas, 58.502 iniciadas/recebidas/completas e 61.498 descartadas pelo scheduler, com `GeneratorLimited` e dados completos. É evidência histórica de um comportamento semelhante, sem reprodução da execução do usuário ou causa exclusiva dos atrasos.
- A faixa configurável até 1.000 req/s não é capacidade comprovada. O envelope sem descartes documentado na 012 é condicionado a 10 req/s no perfil Windows medido. Nenhum novo benchmark foi executado nesta abertura.

## Escopo e sequência

1. Recuperar os parâmetros do relato: runtime Windows/WSL, versão, taxa, duração, concorrência, timeout, tamanho de resposta e resultado exportado sanitizado. Registrar o que significa “metade”: iniciadas, completas, 2xx ou recebidas no servidor. Não registrar credenciais, corpo ou URL completa.
2. Reproduzir com fixture HTTP controlada e contagem independente no servidor. Executar taxa crescente a partir do perfil estável, com três repetições nos pontos relevantes, variando um fator por vez. Comparar execução web e CLI com os mesmos parâmetros; testar alvo rápido, alvo lento, concorrência insuficiente, timeout, 429, 5xx e cancelamento. Separar warm-up e drain.
3. Conciliar em resultados completos: `planejadas = agendadas + não agendadas após parada`; `agendadas = iniciadas + scheduler_late + concurrency_limit + policy`; `iniciadas = HTTP completas + falhas de transporte + timeouts + canceladas em voo + limite de resposta + falhas internas + em voo`. HTTP 4xx/5xx já pertence a HTTP completas e não deve ser somado novamente como terminal. Status pode existir antes de uma falha de leitura do corpo.
4. Comparar recebimento da fixture e inícios do cliente, considerando operações que falham antes de chegar ao servidor. Não inferir recebimento ou processamento de negócio a partir de `Started` ou 2xx. Sob métricas parciais, explicitar a cobertura e a diferença sem fabricar uma causa.
5. Medir atraso/despertares do scheduler, descartes por tolerância/teto/fim, pico em voo, fila HTTP, CPU, memória e ThreadPool do gerador. Investigar a concentração próxima de 50%, inclusive diferenças de espera/timer entre plataformas, antes de propor correção.
6. Corrigir defeitos demonstrados e adicionar regressões pertinentes. Preservar open-loop, concorrência e memória limitadas, sem retries ou reposição silenciosa de chegadas descartadas. Repetir cenários afetados e registrar envelope efetivamente medido.
7. Expor no resultado web, histórico e exportação o funil de contagem, os descartes por causa e os desfechos separados. Mostrar valores, percentuais com denominador, cobertura e evidências do diagnóstico; permitir causas coexistentes e conclusão inconclusiva.

## Dependências e arquivos

Reutilizar M1/M2, [012](FICHA-012-ENGINE-VALIDATION.md), [030](FICHA-030-REACT-LOCAL.md) e [037](FICHA-037-RATE-LIMITING.md). Motor: `src/Dinamico.LoadEngine/{Scheduling,Lifecycle,Metrics,Timing}`; adaptador: `src/Dinamico.Web/{Views,ExecutionService}.cs`; interface: `FrontEnd/src/api.ts`, `RunResult.tsx` e `RunChart.tsx`; persistência apenas se faltar informação necessária ao histórico. Usar testes do motor/web e fixture existentes, sem criar outra fonte de contagem.

## Critérios de aceite e validação

- Cenário reproduzível com ambiente, parâmetros, contagens independentes, dispersão e conclusão: bug demonstrado/corrigido, limitação medida ou investigação inconclusiva com evidência faltante identificada. O relato permanece pendente se apenas um cenário parecido for reproduzido.
- Nenhuma requisição desaparece da conciliação em resultados completos; diferenças em dados parciais aparecem como desconhecidas. Testar descarte por cada causa, erros HTTP, timeout, falha de corpo após headers e cancelamento.
- UI e histórico distinguem não iniciadas, falhas após início, HTTP completas e 2xx; reconexão SSE e recarga não duplicam contagens. `dataComplete=true` não é apresentado como prova de atingir a taxa planejada.
- Evidenciar perda próxima de metade em cenário controlado e cenário sem descartes dentro do envelope medido. Registrar limites do ambiente sem atribuir automaticamente falha ao alvo.
- Executar testes direcionados, build Release, build frontend e verificações de navegador afetadas. Registrar comandos/resultados reais e riscos remanescentes antes de concluir.

## Fora do escopo e status

Não ampliar limites por tentativa, prometer 1.000 req/s, alterar políticas de retry ou iniciar distribuição para esconder o problema. A abertura desta ficha altera apenas documentação; nenhum bug foi confirmado ou corrigido nesta etapa.

## Continuação — testes unitários em 2026-10-25

Atendendo ao pedido de testes, adicionados `tests/Dinamico.LoadEngine.Tests/RequestLossAccountingTests.cs` e `tests/Dinamico.Web.Tests/MetricProjectionTests.cs`. Relógio controlado e transporte em memória demonstram 1.000/1.000 inícios com despertares de 1 ms e 500 inícios/500 descartes por teto com despertares de 4 ms, sem descartes por tolerância ou fronteira e sem saturação da concorrência. Outro caso bloqueia respostas e demonstra 50 inícios/50 recusas por concorrência, sem retry e com conclusão única de cada operação iniciada.

A projeção web distingue os três motivos de descarte de erros do alvo, preserva conciliação e `GeneratorLimited` mesmo com cobertura completa, conta 429/500 uma única vez e não publica latência HTTP completa para timeout após headers. O projeto web concede acesso interno somente ao assembly de testes; nenhuma regra de execução foi alterada.

Validação Windows/.NET SDK 10.0.204: restore locked-mode e build Release aprovados, zero avisos/erros; 79 testes direcionados de motor e seis de projeção web aprovados, zero falhas/ignorados. Comandos: `dotnet.exe restore Dinamico.slnx --locked-mode`; `dotnet.exe build Dinamico.slnx --configuration Release --no-restore`; `dotnet.exe test tests/Dinamico.LoadEngine.Tests/Dinamico.LoadEngine.Tests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~RequestLossAccountingTests|FullyQualifiedName~BoundedAdmissionTests|FullyQualifiedName~ArrivalSchedulerTests|FullyQualifiedName~MetricWindowTests|FullyQualifiedName~BoundedObservabilityTests"`; `dotnet.exe test tests/Dinamico.Web.Tests/Dinamico.Web.Tests.csproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~MetricProjectionTests`.

O build inicial sem restore encontrou pacotes ausentes; após restore locked-mode, o build e os testes foram repetidos com os binários novos. São dez casos novos no conjunto das duas fichas. Não houve novo benchmark, reprodução do relato, teste de banco ou navegador nesta continuação. A ficha permanece aberta.
