# ADR-001 — Motor próprio em .NET 10

Status: Proposto. Data: 2026-10-08.

## Contexto

Produto e aprendizado requerem motor próprio; equipe usa ASP.NET Core/.NET 10.

## Problema

Gerar tráfego com controle de recursos e métricas verificáveis sem construir uma pilha HTTP inteira.

## Alternativas consideradas

k6/outro motor viola requisito confirmado. Go/Rust exigiriam outra stack antes de demonstrar que .NET limita o produto. Implementar sockets/TLS manualmente amplia risco e manutenção.

## Decisão

Usar .NET 10, I/O assíncrono, HttpClient/SocketsHttpHandler, relógio monotônico e motor independente de CLI/Control Plane. Não afirmar capacidade antes da calibração.

## Justificativa

Reutiliza transporte e ferramentas da baseline; permite testar agenda/agregação sem Kubernetes. O motor próprio está na semântica de carga, limites e medição, não em reinventar HTTP/TLS.

## Consequências positivas

Menos linguagens e contratos compartilháveis; transporte maduro, cancelamento e instrumentação do runtime.

## Consequências negativas

GC, timers, alocações e comportamento interno do handler afetam precisão e consumo; requer análise de filas e versões.

## Condições para revisar a decisão

Benchmark reproduzível mostra que orçamento de CPU/RAM ou precisão é inviável após corrigir limitações identificadas. Comparar alternativas no mesmo hardware/perfil antes de migrar.
