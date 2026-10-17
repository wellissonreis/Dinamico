# ADR-007 — Observabilidade sem logs por requisição

Status: política local implementada nas 010/011 (2026-10-17); persistência distribuída futura. Registro original: Proposto, 2026-10-08.

## Contexto

Investigar falhas sem debugger exige evidência; logging por request pode distorcer o teste e vazar dados.

## Problema

Equilibrar capacidade de reconstrução e consumo de CPU/disco.

## Alternativas consideradas

Logar todo request é caro e desnecessário. Só métricas perde causalidade das transições. Stack distribuída completa não se justifica no laboratório inicial.

## Decisão

Logs JSON de mudanças de condição, eventos duráveis no PostgreSQL e métricas agregadas separados. IDs execution/slot/attempt, categorias limitadas, redaction por allowlist e amostras com orçamento. Sem spans individuais da carga por padrão.

## Justificativa

O ciclo de vida e a cobertura explicam falhas com baixo volume; amostras não precisam incluir URL completa ou conteúdo.

## Consequências positivas

Investigação reproduzível e custo limitado mesmo com 100% de erros; menor risco de vazamento.

## Consequências negativas

Não será possível reconstituir cada chamada; stdout tem retenção operacional limitada e exige capturar evidências essenciais antes de apagar Pods.

## Condições para revisar a decisão

Incidentes demonstram falta de evidência ou operação exige consultas de logs multiambiente. Acrescentar coleta/exportação mantendo limites e sem tornar payloads parte do logging.

## Implementação local — 2026-10-17

`BoundedRunObservability` aplica allowlist, resumos por janela, amostras categoria/status, limites 5/100/512 bytes, linha <=4 KiB e supressão de 30 s. Fila 32 e pump independente evitam bloquear geração por stderr lento. Transições latched são publicadas no encerramento. CLI separa stderr do relatório JSON e publica perdas/falhas de observabilidade. Sem banco/eventos duráveis nesta etapa; detalhes e evidências nas [010](../implementation-cards/FICHA-010-BOUNDED-OBSERVABILITY.md) e [011](../implementation-cards/FICHA-011-CLI-REPORT.md). Não encerra calibração M1.
