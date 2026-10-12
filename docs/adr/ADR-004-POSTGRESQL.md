# ADR-004 — PostgreSQL sem OLAP ou broker

Status: Proposto. Data: 2026-10-08.

## Contexto

O produto persiste janelas agregadas, não dezenas de milhões de linhas de requests.

## Problema

Ter catálogo, coordenação recuperável e histórico com poucas dependências operacionais.

## Alternativas consideradas

OLAP beneficia consultas analíticas volumosas, mas adiciona sincronização e operação. Broker não é necessário para polling de poucos Jobs. Memória pura perde intenção no restart.

## Decisão

PostgreSQL para catálogo, fila de execuções, leases, deduplicação e histogramas por janela. Transações curtas, índices dirigidos a consultas e retenção em lotes; sem particionar inicialmente.

## Justificativa

As operações críticas exigem atomicidade e os dados crescem com duração/slots. O documento 04 fornece orçamento para medir o limite real.

## Consequências positivas

Uma fonte durável de verdade, merge idempotente e recuperação sem mensagem externa.

## Consequências negativas

Banco único é limite de ingestão e ponto de falha. Atualizar agregado gera WAL e contenção; retenção requer manutenção.

## Condições para revisar a decisão

Latência de ingestão, locks, custo de armazenamento/VACUUM ou consultas não atendem orçamento após otimizações medidas. Avaliar partição e retenção antes de introduzir OLAP/broker.
