# ADR-007 — Observabilidade sem logs por requisição

Status: Proposto. Data: 2026-10-08.

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
