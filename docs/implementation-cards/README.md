# Índice de fichas

Atualização de 2026-10-22: **M2 concluído no escopo de persistência local**. [013](FICHA-013-POSTGRESQL.md), [014](FICHA-014-IDEMPOTENT-IMPORT.md) e [015](FICHA-015-RESULT-QUERIES.md) entregam PostgreSQL, importação idempotente e consulta/retenção. [Guia e validação](../M2-LOCAL-PERSISTENCE.md). Próximo marco: M3, começando pelo detalhamento da 016. O inventário histórico de M1 abaixo permanece como evidência daquele marco.

Estado atual (2026-10-21): **M1 concluído no escopo HTTP/1.1 local loopback**, fichas 001–012 implementadas e validadas. Espera Windows de alta resolução, diagnóstico do scheduler, recursos GC/ThreadPool/HTTP agregados e calibração reproduzível disponíveis. Envelope demonstrado de 10 RPS no Windows, com limitações WSL registradas na [012](FICHA-012-ENGINE-VALIDATION.md). FICHA-037 conclui sua etapa M1; persistência, Control Plane, distribuição e UI permanecem futuros. Evidências em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md).

Registro histórico de 2026-10-09: FICHA-001 autorizada, implementada e verificada; demais fichas permanecem pendentes. Evidências na própria ficha e instruções de execução no [README do projeto](../../README.md).

Contrato normativo: seção final [Rate Limiting — contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12). Evidências do recorte estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md). Validação histórica da 009 (2026-10-17): 515 testes aprovados, incluindo 19 novos do ciclo/corridas e regressão do sink; 97 direcionados; build Release sem avisos/erros. TRX em `TestResults/Ficha009/ValidatedSuite/`. Os 496 da 008, 472 da 007, 365 da fixture 002, 455 de 13/10 e 119 da base são evidências históricas.

Validação final: **573 testes aprovados em cada runtime Windows/Linux**, 13 direcionados finais, build Release sem avisos/erros e restore locked-mode aprovado. Calibração Windows: 3x30 s, 120 s e 600 s a 10 RPS sem drops; WSL restrito mantém dados completos e sinaliza geração limitada. Ver [012](FICHA-012-ENGINE-VALIDATION.md) para parâmetros, hashes, dispersão, recursos e alcance do aceite M1.

RetryAfterKind default é NotObserved (captura não realizada); Missing exige captura efetiva sem campo em 429, e NotApplicable corresponde a outro status. Não inferir ausência pelo default. HTTP com credenciais sintéticas em loopback é permitido conforme [07](../07-SECURITY.md); o validador não verifica a origem da credencial nem distingue segredos reais de sintéticos. Usar somente segredos sintéticos nesses testes.

## Primeiro marco — motor HTTP local

| Ordem | Ficha | Prioridade | Complexidade | Dependências |
|---:|---|---|---|---|
| 1 | [001 — Contratos e limites](FICHA-001-CONTRACTS.md) | P0 | P | Revisão M0 |
| 2 | [002 — Fixture HTTP controlada](FICHA-002-HTTP-FIXTURE.md) | P0 | P | 001 |
| 3 | [003 — Transporte e streaming](FICHA-003-HTTP-TRANSPORT.md) | P0 | M | 001, 002 |
| 4 | [004 — Medição e deadline](FICHA-004-TIMING-DEADLINES.md) | P0 | M | 003 |
| 5 | [005 — Agenda open-loop](FICHA-005-ARRIVAL-SCHEDULER.md) | P0 | M | 001 |
| 6 | [006 — Admissão e concorrência](FICHA-006-BOUNDED-CONCURRENCY.md) | P0 | M | 004, 005 |
| 7 | [007 — Histograma e percentis](FICHA-007-HISTOGRAM.md) | P0 | M | 001 |
| 8 | [008 — Janelas e contadores](FICHA-008-METRIC-WINDOWS.md) | P0 | M | 006, 007 |
| 9 | [009 — Ciclo de vida local](FICHA-009-LOCAL-LIFECYCLE.md) | P0 | M | 008 |
| 10 | [010 — Logs e amostras](FICHA-010-BOUNDED-OBSERVABILITY.md) | P0 | P | 009 |
| 11 | [011 — CLI e relatório](FICHA-011-CLI-REPORT.md) | P0 | M | 009, 010 |
| 12 | [012 — Calibração e aceite](FICHA-012-ENGINE-VALIDATION.md) | P0 | M | 011 |

## Índice futuro — ainda sem detalhamento

IDs reservados não significam escopo aprovado. Detalhar apenas o próximo marco após medir o anterior.

| ID | Entrega futura | Marco / prioridade / complexidade | Dependências previstas |
|---|---|---|---|
| 013 | Schema, migrations e acesso PostgreSQL | M2 / P0 / M | 012 |
| 014 | Ingestão idempotente de janelas e merge | M2 / P0 / M | 013 |
| 015 | Consulta, resumo e retenção | M2 / P0 / M | 014 |
| 016 | Identity, sessões e convites | M3 / P0 / M | 013 |
| 017 | Workspaces, projetos e autorização | M3 / P0 / M | 016 |
| 018 | Cofre lógico e revisões protegidas | M3 / P0 / M | 017 |
| 019 | Grants de destino e política de conexão remota | M3 / P0 / M | 017, 018 |
| 020 | API de execução, idempotência e quotas | M3 / P0 / M | 015, 019 |
| 021 | Estados/reconciliação com worker local | M3 / P0 / M | 020 |
| 022 | Registro autenticado, heartbeat e lease | M3 / P0 / M | 021 |
| 023 | Imagem de worker isolada e encerramento | M4 / P0 / P | 022 |
| 024 | Helm, RBAC, quotas e NetworkPolicies | M4 / P0 / M | 023 |
| 025 | Adaptador Jobs, evidência e limpeza | M4 / P0 / M | 024 |
| 026 | Suite SSRF/isolamento no Minikube | M4 / P0 / M | 019, 025 |
| 027 | Plano distribuído e barreira de início | M5 / P0 / M | 026 |
| 028 | Consolidação/qualidade distribuída e falhas | M5 / P0 / M | 014, 027 |
| 029 | SSE autenticado e reconexão | M6 / P0 / P | 028 |
| 030 | React: login, projeto e editor de teste | M6 / P0 / M | 018, 020 |
| 031 | React: execução ao vivo e cancelamento | M6 / P0 / M | 029, 030 |
| 032 | React: histórico e resultado parcial | M6 / P0 / P | 015, 031 |
| 033 | Perfil em degraus | M6 / P0 / M | 028, 030 |
| 034 | Backup, restore e rotação de chaves | M7 / P0 / M | 018, 025 |
| 035 | Matriz de falhas e quotas de lançamento | M7 / P0 / M | 028, 032, 033, 034 |
| 036 | HTTP/2 e diagnóstico de streams | Pós-MVP / P1 / M | 035 |

Desenvolver controles de segurança cedo não autoriza exposição pública antecipada: a execução remota depende do gate completo da FICHA-026. Recuperação de falhas já é testada em 014, 021, 022 e 025; 035 consolida cenários.

## Propostas transversais

| ID | Ficha | Prioridade / complexidade | Dependências |
|---|---|---|---|
| 037 | [Identificação e diagnóstico de Rate Limiting](FICHA-037-RATE-LIMITING.md) | P0 / G no conjunto | M1–M6, conforme etapas da ficha; não depende da 036 |

A ficha 037 tem sua etapa local M1 implementada e validada no gate 012. Persistência/controle pertencem a M2/M3, NAT/egress e distribuição a M4/M5, interface a M6; a conclusão integral depende desses marcos.
