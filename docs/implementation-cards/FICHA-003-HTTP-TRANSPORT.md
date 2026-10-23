# FICHA-003 — Transporte HTTP reutilizável e streaming

Prioridade: P0. Complexidade: M. Marco: M1. Status: implementada e validada localmente em 2026-10-21; recursos e integração verificados no gate 012. Registros anteriores são históricos.

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

O transporte agora consome o plano SchemaVersion 2, rejeita schema/políticas desconhecidos e respeita LoadRetryPolicy.None e HttpSuccessRule.Completed2xxV1 do [contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12). Evidências estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md). Ainda falta ligá-lo à agenda e ao ciclo de vida do motor.

Nos headers finais 200–599, capturar uma vez status, valores separados de Retry-After e referência UTC fornecida ao parser. Captura não realizada mantém NotObserved; em 429, captura efetiva sem campo produz Missing; status diferente de 429 produz NotApplicable. Não inferir ausência de Retry-After só por observar 429. Não concatenar/split por vírgula. Passar somente RetryAfterObservation normalizada ao consumidor, sem reter texto bruto; registrar marco de headers separado do terminal. Não tratar 1xx intermediário como final; 101/upgrade não é suportado. Preservar status/IsRateLimited e RetryAfter após falha de corpo; header inválido não muda o desfecho HTTP.

O transporte captura status e Retry-After antes do corpo, preservando ambos em truncamento e cancelamento; testes verificam 429/Retry-After, 503, missing, 200/202, limite de streaming sem Content-Length e cancelamento. Deadline integrado e contagem separada de headers/terminal verificados nas continuações 004/008. Os modos no ciclo local e canários/redação foram exercitados nas continuações 009–012; duplicados/datas/clamp via transporte constam na continuação de 2026-10-21. O gate final permanece pendente.

## Decisões técnicas

SocketsHttpHandler/HttpClient, ResponseHeadersRead, proxy/redirect/cookies/decompressão automática desabilitados, pool de vida explícita. Um buffer por operação ativa, devolvido em finally; não compartilhar HttpRequestMessage. Timeout total já integrado pela 004, mantendo suporte a token.

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

- [x] Todos os caminhos liberam recursos e passam token ao I/O.
- [x] Testes reais de streaming/pool/limites aprovados.
- [x] Configuração efetiva do handler consta no plano.

## Evidência histórica parcial — transporte local (2026-10-12)

Implementação em `src/Dinamico.LoadEngine/Transport/LocalHttpTransport.cs`. Um handler/cliente reutilizável por plano usa HTTP/1.1 exato, conecta ao IP loopback fixado mantendo a URI original, desabilita proxy/redirect/cookies/descompressão e limita conexões, headers, pool e corpo. Cada request/response é descartado; payload usa o buffer imutável do plano e o buffer de leitura é devolvido ao ArrayPool. Cancelamento acompanha conexão/envio/leitura. Duração é monotônica, mas timeout total por operação e integração de deadline continuam na FICHA-004.

Testes reais com FICHA-002 verificam reuso da conexão, payload POST, 429 com Retry-After seguido de truncamento, Missing/NotApplicable, streaming chunked até limite, cancelamento após headers, redirect não seguido e sucesso HTTP 200/202. Build/test da solução: 371 testes aprovados antes da ampliação dos casos 200/202 e HEAD; validação final consta no registro da continuação.

## Continuação 008 — 2026-10-17

`SendAsync(index, token, observeHeaders)` entrega `ResponseHeadersObservation` validada com timestamp monotônico/status/Retry-After uma vez antes de consumir o corpo. A sobrecarga anterior permanece compatível. Falha do observador escapa como `TransportObserverException`; não é convertida em sucesso HTTP nem ocultada por classificação de erro de rede. Testes com corpo bloqueado e deadline controlado verificam status na janela anterior ao terminal; detalhes e validação em [008](FICHA-008-METRIC-WINDOWS.md). Transporte permanece parcial; não há host integrado ou fechamento do gate 012.

## Continuação 009 — 2026-10-17

Ciclo local agora possui o pool por execução, registra início/headers/terminal e dispõe o transporte após observar todas as operações. Testes HTTP reais com fixture (agenda simulada) cobrem 202/429/503 no ciclo; cancelamento/drain e adaptação não cooperativa têm vetores específicos em [009](FICHA-009-LOCAL-LIFECYCLE.md). Interface interna ILocalRunTransport é fronteira de teste, sem configuração pública para substituir políticas de rede. Permanecem vetores restantes de transporte e gate 012; CLI ainda informativa.

## Continuação 010–012 — 2026-10-17

CLI funcional e relatório agora exercitam o transporte em subprocesso contra a fixture: 202/429/503 nos três modos, canários, corpo limitado 200/429, deadline após headers 429 e Retry-After duplicado, cancelamento POSIX e conciliação de contagens. Status permanece separado de terminal e 503 não vira rate limiting. Não encerra esta ficha: continuam pendentes os vetores remanescentes/recursos e gate de calibração completo da [012](FICHA-012-ENGINE-VALIDATION.md). Os trechos anteriores que dizem não haver host são históricos.

## Continuação — vetores reais de transporte (2026-10-21)

`LocalHttpTransportTests` agora cobre 500 como resposta HTTP concluída sem sucesso, corpo vazio, 204/205/304, rejeição por Content-Length acima do teto antes da leitura e streaming acima do teto. Ambos os limites preservam 429/Retry-After e são seguidos de resposta 200 completa no mesmo transporte com uma conexão por servidor. Cancelamento antes dos headers mantém status ausente/NotObserved; cancelamento no corpo preserva headers. Ambos permitem uma operação posterior completa e retornam a fixture a zero operações ativas. Token previamente cancelado não abre conexão e uma porta loopback reservada sem listener produz falha Connect sem headers.

Retry-After real cobre data futura/passada, clamp de data e delta, valor inválido, lista em um campo e campos duplicados, com referência UTC fixa e observação de headers única. O teste de proxy executa a CLI em subprocesso com HTTP(S)/ALL_PROXY configurados e NO_PROXY vazio somente no ambiente filho; a fixture destino recebe respostas completas e a fixture proxy recebe zero conexões. Truncamento e cancelamento no corpo aguardam agora o callback de headers antes de interromper a operação. A fixture recebeu apenas tamanho alternado de corpo, validado e limitado, para testar recuperação sem trocar o pool.

Validação direcionada: SDK Windows .NET 10.0.204, Release, `dotnet.exe test tests/Dinamico.LoadEngine.Tests/Dinamico.LoadEngine.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~LocalHttpTransportTests|FullyQualifiedName~HttpFixtureTests' --logger trx --results-directory TestResults/M1/Transport`: **83 aprovados, zero falhas**, incluindo compilação dos projetos referenciados. TRX: `TestResults/M1/Transport/welli_WELL-PC_2026-10-21_19_49_25.trx`. `git diff --check` aprovado. O primeiro cenário de proxy, com duas chegadas em um segundo, não produziu tráfego na execução Windows; o teste passou com dez chegadas por segundo durante dois segundos, consistente com os cenários reais existentes da CLI, e exige tráfego e conciliação de contagens. Esta evidência não aprova o gate M1 nem substitui calibração, varredura de recursos ou validação final da solução na FICHA-012.

Fechamento: suíte final de 573 testes em cada runtime Windows/Linux, incluindo liberação da conexão idle ao dispor o transporte e retorno das conexões a zero após cancelamento. Ensaios de 10 min Windows e 2 min WSL restrito preservam contagens e encerram conexões; memória/handles e limites na [012](FICHA-012-ENGINE-VALIDATION.md). Aceite restrito ao transporte local existente, sem ampliar destinos/protocolos.
