# ADR-006 — REST com polling, leases e ACK durável

Status: Proposto. Data: 2026-10-08.

## Contexto

Workers enviam poucas janelas e comandos em relação ao volume de tráfego gerado.

## Problema

Coordenar e recuperar controle/telemetria sem infraestrutura de mensagens e sem travar hot path.

## Alternativas consideradas

gRPC streaming/WebSocket pode reduzir overhead, mas adiciona estado de conexão/reconexão. Broker fornece entrega, porém não elimina dedup e custa operação. SSE é adequado para UI, não necessário para protocolo bidirecional do worker.

## Decisão

REST HTTPS versionado; heartbeat/poll de 2 s, lease de tráfego de 10 s, deltas imutáveis a cada 5 s, buffer limitado e retry idêntico. ACK após commit; stop na perda prolongada de controle/telemetria. SSE entrega snapshots para o navegador.

## Justificativa

Reutiliza infraestrutura HTTP e permite testar cada mensagem. Dados duráveis podem ser reconsultados de qualquer réplica do CP.

## Consequências positivas

Protocolo visível, deduplicável e independente de afinidade de conexão.

## Consequências negativas

Polling adiciona latência de cancelamento, requisições periódicas e possível sincronização de retries; jitter e limites são necessários.

## Condições para revisar a decisão

Latência exigida ou volume de workers torna polling/HTTP o gargalo medido. Não migrar sem preservar fencing, ACK durável e limites de fila.
