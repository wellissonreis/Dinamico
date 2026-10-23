# FICHA-004 — Duração monotônica e deadline por operação

Prioridade: P0. Complexidade: M. Marco: M1. Status: implementada em 2026-10-13; integração M1 validada no gate 012 de 2026-10-21.

## Objetivo

Medir duração consistente e encerrar chamadas que ultrapassem seu deadline, incluindo leitura do corpo.

## Contexto arquitetural

Acrescenta medição/classificação ao transporte da 003; não controla a duração global do teste.

## Conceitos necessários

Relógio civil pode saltar; monotônico mede intervalos. Timeout é limite de observação, não tempo de resposta que o servidor necessariamente teria. Cancelamento solicitado não equivale a timeout.

## Responsabilidades

Capturar start/headers/end, aplicar deadline total e connect timeout, combinar cancelamento externo com timeout e produzir exatamente um desfecho terminal.

## Fora do escopo

Histograma, cancelamento global, CP, tempo sincronizado entre hosts e decomposição inventada de DNS/TLS.

## Dependências

FICHA-003.

## Contratos

Entrada: request validado, deadline e token de parada. Saída: `RequestObservation` limitada com duração, status quando disponível, bytes e categoria exclusiva. `http_completed_duration` existe somente após EOF; elapsed de timeout permanece na população terminal.

## Integração futura — Rate Limiting v2

Medição/deadline implementados em `Timing/OperationDeadline.cs` e integrados ao transporte. Seguir o [contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12); evidências estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md).

Preservar status 429, IsRateLimited e RetryAfterObservation após timeout, cancelamento ou falha de leitura, com um só desfecho terminal. Falha ou cancelamento não converte NotObserved em Missing: preservar a evidência capturada, ou a indicação de captura não realizada. 429 completo entra na população HTTP completa, mas não em http_success; 2xx incompleto não satisfaz Completed2xxV1. O instante UTC dos headers serve somente ao parser de datas; duração/deadline continuam monotônicos. Retry-After não cria timer de espera ou nova tentativa.

Testar 429 com headers rápidos/corpo parado, EOF concorrente com timeout/cancelamento, 503 com Retry-After sem classificação de limitação e salto de UTC sem efeito na duração. A incerteza de relógio da data deve permanecer explícita. Esses cenários estão cobertos pela suíte da retomada; ver evidências abaixo.

## Decisões técnicas

Stopwatch e fronteira mínima de relógio; HttpClient.Timeout infinito com deadline explícito cobrindo envio/leitura. Primeiro motivo observado latched para corrida de timeout/cancelamento. Sem exceções para HTTP 4xx/5xx.

## Passos de implementação

1. Definir conversão verificada entre ticks e unidade do contrato.
2. Aplicar deadline e ligar cancelamento externo.
3. Registrar marcos sem alocação de evento/log individual.
4. Mapear falhas por fase observável e preservar um só terminal.
5. Garantir descarte da fonte de cancelamento.

## Testes obrigatórios

Headers rápidos com corpo parado excedem timeout; conectar sem êxito respeita o teto; cancelar antes/durante envio/leitura; timeout concorre com EOF sem dupla contagem; mudança de UTC não altera duração. Usar relógio controlado para matemática e fixture real para I/O.

## Observabilidade

Categoria e fase de erro, duração e status opcionais; nenhum texto bruto de exception com URL. Duração negativa é falha interna.

## Critérios de aceite

Toda operação observada tem um desfecho e duração monotônica não negativa. Deadline cobre corpo e diferença de motivos é testada. A tolerância de término em teste real é explícita e considera scheduler do SO, sem prometer interrupção instantânea.

## Riscos e trade-offs

I/O cancelável é cooperativo; pausas do processo afetam precisão. Timeouts censuram latência e não podem ser apresentados como respostas completas.

## Definição de pronto

- [x] Sem timers/fontes de cancelamento retidos após conclusão.
- [x] Corridas de término e cancelamento verificadas.
- [x] Populações de duração aderem a 03.

## Evidência da retomada

`TimingDeadlineTests.cs` cobre conversão inteira verificada, ordenações de EOF/timeout/cancelamento, timer atrasado, saltos de UTC, descarte e I/O real. Recuperado código pendente e validado em Release; detalhes no registro de entrega. Agregação das populações depende de 007/008.
