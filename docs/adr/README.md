# Architecture Decision Records

Todos os registros estão **Propostos**, em 2026-10-08, aguardando revisão da documentação. Confirmar uma decisão não autoriza implementação nesta entrega.

| ADR | Decisão | Documento principal |
|---|---|---|
| [001](ADR-001-DOTNET-ENGINE.md) | Motor próprio em .NET 10 | Motor |
| [002](ADR-002-MODULAR-MONOLITH.md) | Control Plane como monólito modular | Sistema |
| [003](ADR-003-KUBERNETES-JOBS.md) | Job por slot com exclusividade na aplicação | Sistema/ciclo de vida |
| [004](ADR-004-POSTGRESQL.md) | PostgreSQL para coordenação e agregados | Dados |
| [005](ADR-005-METRICS-HISTOGRAMS.md) | Janelas e histograma logarítmico fixo | Motor/dados |
| [006](ADR-006-WORKER-COMMUNICATION.md) | REST, polling e ACK após commit | Ciclo de vida |
| [007](ADR-007-OBSERVABILITY.md) | Logs limitados, eventos e métricas separados | Observabilidade |
| [008](ADR-008-OPEN-LOOP.md) | Open-loop e partição fixa de chegadas | Motor |
| [009](ADR-009-TARGET-SECURITY.md) | Destino comprovado, IP pinado e egress restrito | Segurança |
| [010](ADR-010-FAILURE-SEMANTICS.md) | Parar diante de perda de worker/controle | Ciclo de vida |

Uma revisão deve citar evidência e atualizar documentos/contratos relacionados. Guardar ADR antigo como Superseded se a decisão mudar; não apagar a justificativa histórica.
