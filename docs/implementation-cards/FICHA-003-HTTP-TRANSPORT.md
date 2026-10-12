# FICHA-003 — Transporte HTTP reutilizável e streaming

Prioridade: P0. Complexidade: M. Marco: M1. Status: proposta de integração futura; não implementada.

## Objetivo

Uma chamada deve consumir a resposta com memória limitada e liberar recursos em todos os desfechos, reutilizando conexões entre chamadas.

## Contexto arquitetural

Implementa a fronteira de transporte do motor; agenda e métricas serão adicionadas por outras fichas.

## Conceitos necessários

HttpClient reutiliza pool; request/response têm ownership por chamada. `ResponseHeadersRead` evita buffer do corpo inteiro, mas exige leitura, limite e cancelamento explícitos. TLS não deve perder hostname por pinning de IP.

## Responsabilidades

Handler/client por execução, HTTP/1.1 exato, streaming com buffer limitado, pinning loopback no host local, headers/payload imutáveis e descarte correto. Receber CancellationToken desde o início.

## Fora do escopo

Scheduler, cálculo de deadline, retries, redirects, cookies, HTTP/2 e destinos públicos.

## Dependências

FICHAS 001 e 002.

## Contratos

Entrada: configuração validada e token. Saída: status opcional, bytes consumidos e desfecho de transporte; callbacks/marcos para headers e término sem expor corpo. Limites: response 1 MiB, buffer 16 KiB, conexões no máximo C; todos definidos no plano.

## Integração futura — Rate Limiting v2

Transporte permanece pendente. Consumir plano SchemaVersion 2, rejeitar schema desconhecido e respeitar LoadRetryPolicy.None e HttpSuccessRule.Completed2xxV1 do [contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12). Evidências da extensão pura estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md).

Nos headers finais 200–599, capturar uma vez status, valores separados de Retry-After e referência UTC fornecida ao parser. Captura não realizada mantém NotObserved; em 429, captura efetiva sem campo produz Missing; status diferente de 429 produz NotApplicable. Não inferir ausência de Retry-After só por observar 429. Não concatenar/split por vírgula. Passar somente RetryAfterObservation normalizada ao consumidor, sem reter texto bruto; registrar marco de headers separado do terminal. Não tratar 1xx intermediário como final; 101/upgrade não é suportado. Preservar status/IsRateLimited e RetryAfter após falha de corpo; header inválido não muda o desfecho HTTP.

Verificar com fixture 429 completo e seguido de truncamento/timeout/cancelamento, 503 com Retry-After = NotApplicable sem sinal de limitação, duplicados/datas/clamp e 200/202 completo versus corpo incompleto. Provar ausência de retry/espera/adaptação em todos os modos. Enviar Headers validados, inclusive credenciais sintéticas Normal/Diagnostic/Authorized em HTTP loopback conforme [07](../07-SECURITY.md), sem logs ou canários vazando. O validador não verifica a origem da credencial nem distingue segredos reais de sintéticos. Status deve ser contado uma única vez pela futura 008 nos headers, nunca novamente no terminal. Não há testes executados nesta atualização.

## Decisões técnicas

SocketsHttpHandler/HttpClient, ResponseHeadersRead, proxy/redirect/cookies/decompressão automática desabilitados, pool de vida explícita. Um buffer por operação ativa, devolvido em finally; não compartilhar HttpRequestMessage. Timeout total virá de 004, mantendo suporte a token nesta ficha.

## Passos de implementação

1. Construir cliente e política de conexão loopback pinada.
2. Criar requests próprios com payload referenciado imutável.
3. Consumir corpo até EOF ou teto, sem string/log.
4. Dispor conteúdo/response/request e buffers inclusive em falhas.
5. Expor marcos e resultado limitado ao consumidor.

## Testes obrigatórios

Reuso com fixture keep-alive; 500 tratado como resposta; HEAD/corpo vazio; payload entregue corretamente; corpo grande com/sem Content-Length; truncamento; cancelamento no read; redirect não seguido; proxy ambiente ignorado; nenhum destino fora do loopback conectado.

## Observabilidade

Contadores fornecidos ao chamador: bytes, status, erro categorizado e criação de conexões quando observável. Sem logging por request.

## Critérios de aceite

Consumo do corpo não cresce proporcionalmente ao tamanho total recebido. Recursos liberados após erro/cancelamento. Requisições sequenciais reutilizam conexão quando a fixture permite; falhas não geram retry do motor.

## Riscos e trade-offs

Interromper corpo acima do teto pode descartar conexão. Pool não elimina limite de portas do host. Não confundir headers recebidos com operação concluída.

## Definição de pronto

- [ ] Todos os caminhos liberam recursos e passam token ao I/O.
- [ ] Testes reais de streaming/pool/limites aprovados.
- [ ] Configuração efetiva do handler consta no plano.
