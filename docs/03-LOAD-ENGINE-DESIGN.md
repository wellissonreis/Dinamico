# 03 — Projeto do motor de carga

Status atual (2026-10-17): contratos locais v2, fixture 002, deadline 004, agenda 005, admissão 006, histograma 007, contadores/janelas 008 e ciclo local 009 implementados; transporte 003 parcial. Observabilidade 010 e CLI/relatório 011 implementados; calibração/aceite 012 pendentes. Semântica canônica das métricas deste SaaS.

## Comportamento esperado

Dado um endpoint permitido, RPS inteiro, duração inteira em segundos e limites de recursos, o motor planeja chegadas independentes da velocidade de resposta. Inicia apenas o trabalho que cabe nos limites; registra o restante como não iniciado. Termina a geração na hora planejada, drena ou cancela operações, fecha métricas e produz um resultado com qualidade explícita.

O marco local aceita um endpoint, perfil constante e HTTP/1.1. Perfil em degraus do MVP será uma sequência de segmentos com RPS e duração inteiros; zero RPS significa pausa. Uma revisão fixa o perfil, método, protocolo, timeout, limites, regra de sucesso HTTP e versão do motor. Não adaptar a taxa conforme a resposta do alvo. Uma ferramenta de busca adaptativa de capacidade seria outro produto/experimento.

## Open-loop, taxa e concorrência

Num modelo closed-loop, 100 clientes enviam a próxima requisição depois da resposta anterior. Se a latência sobe, o RPS cai automaticamente. Neste produto, a pergunta é outra: “o que acontece se chegarem 1.000 pedidos por segundo?”. Por isso a agenda é independente de conclusões.

Para um segmento de taxa R e duração D, N = R × D. A chegada de índice local n, de 0 a N−1, tem instante relativo `(n + 0,5) / R`. A defasagem de meio intervalo evita a ambiguidade de uma chegada exatamente no término. Usar aritmética inteira/racional para índices e offsets de ticks, com multiplicação verificada contra overflow; não acumular erros somando delays sucessivos. Segmentos de R=0 não têm chegadas. O índice global continua entre segmentos.

No marco local existe um slot. Com W workers, a chegada global n pertence ao slot `n mod W`. Para R=10 e W=3 em um segundo, os slots recebem 4, 3 e 3 chegadas, em instantes intercalados. Não arredondar 10/3 em cada worker. R<W é permitido: alguns slots não enviam naquela janela. Cada worker calcula somente seus índices; não varre toda a agenda global.

O plano garante que a soma das **chegadas atribuídas** é a taxa pedida. Não garante taxa física exata de TCP, HTTP recebido ou resposta. GC, escalonamento do sistema operacional, rede e limites introduzem atraso. Reportar pedido, iniciado, concluído e não iniciado separadamente. A escolha de W é anterior à execução e baseada na capacidade aferida do mesmo perfil de worker e da mesma classe de payload/protocolo.

## Scheduler e backpressure

Usar um loop assíncrono com relógio monotônico (`Stopwatch.GetTimestamp`, exposto por uma pequena fronteira testável). `TimeProvider` pode sustentar testes do host, sem exigir um relógio UTC para latências. UTC só alinha T0 entre máquinas; após calcular o offset, usar tempo monotônico local. Mudanças posteriores no relógio civil não deslocam a agenda.

Hipótese inicial: quantum de despertar de 1 ms, atraso máximo admissível de 5 ms, nenhum busy-spin. Não criar `Task.Delay` ou timer para cada requisição e não prometer precisão de microssegundos. Em um despertar, processar as chegadas vencidas com atraso aceitável, limitado a `ceil(R_local_max × 0,001) + 1` admissões, calculando R_local_max conservadoramente a partir do plano. Excedentes daquele despertar viram `not_started.scheduler_late`; não ficam acumulados para o próximo ciclo. Saltar intervalos perdidos por aritmética de índices, sem um loop proporcional a milhões de drops.

Exemplo: a 50.000 RPS globais, um único gerador precisaria admitir aproximadamente 50 chamadas a cada milissegundo. Isso já constitui microbateladas, não tráfego perfeitamente uniforme. O benchmark mede distribuição de inícios em 1 ms, 100 ms e 1 s. Caso a granularidade ou overhead seja inadequado, revisar quantum/particionamento após medir, sem esconder a limitação.

Antes de `SendAsync`, tentar obter um slot de concorrência **sem esperar**. Não disponível: contar `not_started.concurrency_limit`. Não há fila de requisições aguardando resposta anterior. O loop acompanha no máximo C operações e observa todas as exceções. Usar I/O assíncrono, sem uma thread por conexão nem `Task.Run` em torno de cada chamada de rede. Um channel ilimitado apenas moveria o problema para RAM e latência.

Backpressure de telemetria é diferente: preservar janelas imutáveis até ACK dentro do limite de 6 janelas/256 KiB. Ao não conseguir reservar espaço para a próxima janela, parar novas chegadas com `telemetry_backpressure`, preservar o que cabe e declarar resultado parcial. O reporte desse erro tem canal/slot de controle reservado; se também falhar, o CP detecta lease/final ausente. Não descartar silenciosamente métricas para continuar um teste sem evidência.

## Transporte HTTP e conexões

Um `SocketsHttpHandler` e um `HttpClient` de carga por execução/destino, reutilizados até o drain. Cada operação cria request/content próprios; não compartilhar `HttpRequestMessage` ou mutar `DefaultRequestHeaders` durante execução. Payload estático é um buffer imutável limitado, codificado uma vez, sem copiar o corpo inteiro a cada envio. Reusar clientes evita recriação de pools e pressão de portas; a vida do pool é uma configuração explícita. [Diretrizes HttpClient](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/httpclient-guidelines).

HTTP/1.1 com política de versão exata no primeiro marco torna interpretação de conexões mais simples. `MaxConnectionsPerServer=C` inicialmente; C é limite de chamadas simultâneas do motor. Desabilitar redirects, cookies automáticos, proxy herdado do ambiente e descompressão automática. Validar certificado TLS normalmente. Não expor opção “ignorar TLS” na configuração do SaaS. Status 3xx é observado como resposta; não será seguido.

`PooledConnectionLifetime` proposto de 2 minutos, ajustável após ensaios de DNS/TLS; idle timeout e connect timeout explícitos no snapshot (hipóteses 30 s e 3 s, este limitado pelo timeout total). Mesmo com keep-alive, o alvo pode fechar conexões, e NAT/conntrack podem saturar antes de CPU. Limitar origens a uma impede pools por URLs arbitrárias. A política de conexão valida IP em cada nova conexão, conforme 07; preservar hostname/SNI e verificação TLS.

Usar `ResponseHeadersRead` e ler corpo até EOF para um buffer de 16 KiB reutilizado por operação ativa. Descartar bytes após contabilizar, sem transformar em string. Limite inicial de resposta: 1 MiB; request: 64 KiB; headers de request: 16 KiB no total; limite de headers de resposta configurado no handler. Aplicar limite mesmo sem Content-Length, e rejeitar imediatamente tamanho anunciado acima do teto. Corpo acima do limite é erro do gerador/configuração, interrompe aquela resposta e pode impedir reuso da conexão.

Com `ResponseHeadersRead`, o timeout do `HttpClient` não cobre automaticamente toda a leitura do corpo. A proposta usa `HttpClient.Timeout` infinito e um deadline próprio por operação, passado a envio **e** leitura; timeout de conexão continua sendo um teto adicional. [Semântica oficial de completion/timeout](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0).

Dispor request, response, content e fonte de cancelamento por operação em todos os caminhos, devolver buffers e liberar concorrência em `finally`. Dispor handler apenas depois que todas as operações forem observadas. Não chamar `EnsureSuccessStatusCode`: uma resposta 500 é dado do teste. Sem retries adicionados pelo motor; possíveis recuperações internas do transporte .NET não fazem de `started` uma contagem exata de mensagens no fio. Registrar versão do runtime e conferir contagem recebida pela fixture.

## Tempo, timeout e cancelamento

Marcos por operação, capturados com relógio monotônico:

- `due`: chegada planejada.
- `start`: imediatamente antes do envio, após admissão.
- `headers`: retorno dos headers, quando disponível.
- `end`: EOF ou desfecho por erro/cancelamento.

`dispatch_lateness=start−due` mede atraso do gerador. `terminal_duration=end−start` inclui fila interna do handler, DNS, conexão, TLS e leitura que efetivamente acontecerem. `http_completed_duration` usa o mesmo intervalo apenas quando o corpo terminou. `headers−start` pode ser uma estatística operacional agregada, mas não será apresentado como TTFB puro: inclui obtenção e processamento dos headers.

Não subtrair a fila do handler para produzir “latência do servidor”; o motor não observa esse tempo diretamente. Somar percentis de DNS/TLS/servidor também seria incorreto. Não aplicar correção automática de coordinated omission sobre histogramas: ela imputaria observações inexistentes. Open-loop, lateness e drops tornam explícitas as chegadas que não foram medidas.

Fim natural da duração para novos envios; chamadas em voo continuam até seu deadline (no máximo 60 s após o último início). Cancelamento do usuário, SIGTERM ou lease expirado param a agenda e sinalizam cancelamento das chamadas ativas imediatamente. Distinguir razão de parada, timeout de requisição, timeout de conexão e interrupção do processo. Em corrida, classificar pelo primeiro motivo latched no controlador da operação; não usar apenas o tipo `TaskCanceledException` como explicação. Corridas não podem duplicar contagem ou liberar slot duas vezes.

Implementação local 009 (2026-10-17): `LocalRunController.RunAsync(plan, output, token)` integra agenda, admissão, pool HTTP, headers e terminal ao acumulador. Uma execução por controlador; `ILocalMetricOutput.WriteAsync` confirma janelas e `FlushAsync` confirma o prefixo/manifesto. Janela permanece no sink até confirmação, contando entre as seis reservadas; encoding/I/O ficam fora do lock de métricas. Rotação periódica de 5 s continua no drain. O controlador reconcilia due no instante latched de parada, incluindo as vencidas desde o último poll, separando policy/scheduler_late de not_scheduled_after_stop.

Fim natural não cancela I/O. Primeira parada anormal é latched, falhas posteriores ficam em máscara separada; cancelamento durante drain continua válido. `Completion`, `Reason`, qualidade, cobertura, operações drenadas, saída encerrada e flush confirmado são campos distintos. Resultados podem descrever execução falha com dados completos ou prefixo parcial sem extrapolação. Flush tem deadline de 10 s após fechar métricas, com grace de 1 s para cancelamento; drain tem deadline de transporte e fallback RequestTimeout + 1 s, mais grace de 1 s. Adaptador injetado não cooperativo é marcado incompleto, com observação de falha tardia e dispose adiado até terminar a operação. Nenhum pool é declarado encerrado enquanto a operação permanece ativa. Contratos/evidências na [FICHA-009](implementation-cards/FICHA-009-LOCAL-LIFECYCLE.md). SIGINT/SIGTERM Linux em subprocesso, observabilidade e relatório implementados nas 010/011; calibração/aceite permanecem na 012.

## Contadores e populações

As identidades abaixo valem no total acumulado fechado, não em cada janela, porque uma chamada pode iniciar e terminar em janelas diferentes.

| Campo | Definição |
|---|---|
| `planned_total` | Total do plano original, inclusive trecho não executado por parada |
| `scheduled_due` | Chegadas cujo horário foi alcançado antes da parada da geração |
| `started` | Operações admitidas para chamar transporte |
| `not_started` | Due descartadas; subcategorias exclusivas late/concurrency/policy |
| `not_scheduled_after_stop` | `planned_total − scheduled_due`, não tratado como drop de capacidade |
| `http_completed` | Respostas com corpo consumido integralmente, inclusive 4xx/5xx |
| `transport_failed` | DNS, conexão, TLS, reset, protocolo ou leitura, sem timeout/cancelamento |
| `request_timed_out` | Deadline expirou; distinguir fase quando observável |
| `cancelled_in_flight` | Operação abortada por parada da execução |
| `response_limit_exceeded` | Headers/corpo excederam limites |
| `internal_failed` | Falha inesperada do motor na operação, que também interrompe execução |
| `status_received[100..599,other]` | Contagem ao receber headers; pode existir mesmo se o corpo falhar |

`scheduled_due = started + not_started`.

`started = in_flight + http_completed + transport_failed + request_timed_out + cancelled_in_flight + response_limit_exceeded + internal_failed`.

`planned_total` e `not_scheduled_after_stop` são metadados do plano/resumo final, não deltas repetidos para somar a cada janela. Os outros contadores de evento são deltas; `in_flight` é gauge. Essa distinção faz parte do schema, inclusive na consolidação de múltiplos workers.

Uma falha anterior à chamada do transporte é `not_started.policy` e deve interromper a execução se indicar quebra de invariante. Em crash não recuperável, as igualdades só podem ser avaliadas sobre o trecho comprovado; não inventar zeros para o restante.

`http_success` é subconjunto de `http_completed`, pela regra de status configurada (default 200–299). Taxa de falha usa como denominador operações terminadas, com definição publicada, e não mistura drops de geração com HTTP 500. Informar separadamente porcentagem de carga não iniciada e cobertura das métricas. Em resultados parciais, todos os totais são “observados”.

## Histograma e P50/P95/P99

Schema implementado na FICHA-007, sem pacote adicional: histograma logarítmico fixo `log2-32-v1`, com limites superiores `u_i = 1 microssegundo × 2^(i/32)` para i=0..832. A tabela materializada em `Metrics/LogHistogramSchema.cs` é canônica, em nanossegundos inteiros arredondados para cima e validada por versão. Cada limite é o menor inteiro u tal que `u^32 >= 1000^32 × 2^i`; não usar ponto flutuante no runtime. São 833 buckets finitos, primeiro de 0 até 1 µs, demais abertos à esquerda e fechados à direita, mais um bucket overflow: **834 contadores de 64 bits, 6.672 bytes por histograma**. Último limite: 67,108864 s, acima do teto de timeout de 60 s. Pausas podem ultrapassá-lo e devem aparecer em overflow.

Manter dois histogramas por janela: `terminal_duration` e `http_completed_duration`. Reportar P50/P95/P99 principal da segunda população com N explícito. O primeiro ajuda a mostrar timeouts e cancelamentos, mas seus valores são durações até interrupção, não o tempo que o servidor levaria para responder. Se não houver observações, percentil é `null`, nunca zero.

Para percentil p, calcular rank `ceil(p × N)` e encontrar o primeiro bucket cuja soma acumulada alcance o rank. Retornar seu limite superior e declarar aproximação. Acima de 1 µs e fora do overflow, o excesso relativo é menor que `2^(1/32)−1`, aproximadamente 2,19%, acrescido do arredondamento de até 1 ns; no primeiro bucket erro absoluto é de até 1 µs. Percentil em overflow retorna “> 67,108864 s”, sem inventar valor finito.

Somar buckets compatíveis para consolidar workers/janelas. Por exemplo, um worker com 9.900 respostas a 10 ms e outro com 100 a 1 s tem P95 global próximo de 10 ms, e não a média de seus P95. Manter contagem, soma de durações com overflow verificado e mínimo/máximo observados; média global é soma/contagem, não média não ponderada de médias.

Decisão de representação da FICHA-007 (2026-10-17): duração de entrada, soma e contagens são UInt64, em nanossegundos; mínimo/máximo são null sem observações. Registro e merge detectam overflow e não alteram os operandos em caso de falha; merge é puro. P50/P95/P99 usam rank inteiro exato, com UInt128 intermediário. O histograma possui array fixo de 834 contadores (6.672 bytes mais metadados/overhead); a tabela finita compartilhada ocupa 6.664 bytes. Registro e consulta não alocam por observação. Sincronização e associação às populações/janelas implementadas na 008; integração ao ciclo na 009.

Codec `HistogramCodec` v1: exatamente 6.760 bytes little-endian, incluindo os 6.672 bytes de contadores, cabeçalho de 56 bytes e SHA-256 de 32 bytes. Schema numérico 1 significa somente `log2-32-v1`. Layout, offsets e decisão estão na [FICHA-007](implementation-cards/FICHA-007-HISTOGRAM.md#decisão-de-implementação--2026-10-17). Tamanho diferente, versão/schema desconhecido, corrupção, contagens/soma/extremos incoerentes ou overflow são erros de integridade; não converter ou substituir por vazio. Checksum não autentica o remetente. O codec limita a alocação e valida antes de construir o histograma; não implementa ingestão remota.

Alternativas: amostras individuais tornam memória proporcional a requests; buckets lineares perdem resolução ou usam muita RAM; t-digest implica complexidade e características de erro diferentes; HDR oferece maior precisão configurável e é candidato se 2,19% não atender. A decisão atual aceita quantis aproximados para manter limites e algoritmo auditável. Uma adoção futura de HDR exigirá avaliar implementação .NET, manutenção, licença e serialização, não reescrever HDR do zero. [Projeto original HDR](https://github.com/HdrHistogram/HdrHistogram).

## Agregação concorrente e janelas

Uma seção crítica curta protege atualizações de contadores/histogramas e rotação a cada 5 s. Nunca fazer I/O, logging, encoding ou merge de rede dentro desse lock. Classificação do bucket é feita antes do lock. A rotação troca o acumulador ativo por outro zerado; o acumulador fechado é imutável. No primeiro marco, preferir esse desenho verificável. Se contenção for medida, shards fixos por executor são alternativa, com custo de memória e merge explícito.

Janela `k=floor(elapsed/5 s)` relativa a T0 monotônico. A atribuição usa o instante monotônico de **registro serializado do evento** no acumulador, capturado dentro da seção crítica. Duração HTTP usa os marcos capturados junto ao I/O, fora do lock. Assim uma conclusão atrasada na disputa do lock pode pertencer à janela seguinte, sem reabrir janela imutável nem inflar a duração HTTP. Medir atraso entre observação e registro para detectar contenção. Não há fila assíncrona ilimitada de observações.

Inícios, status e conclusões são registrados independentemente; um request atravessando janelas não é dividido entre histogramas. In-flight no fim da janela é gauge, não contador somável no tempo. Após cessar a geração, janelas de drain continuam existindo, inclusive janela parcial final. Emitir janelas vazias até o encerramento efetivo do worker para provar cobertura; não interpretar ausência como zero. Manifesto informa último índice e intervalo parcial. O CP só considera zero após o término comprovado daquele worker; para janela de drain global não exige dados de um worker cujo fim já foi confirmado.

O total final da execução soma todas as janelas, inclusive drain. RPS iniciado médio usa duração da geração; throughput de conclusão por janela usa largura real da janela; o relatório distingue tempo de carga de tempo até finalização. Não dividir conclusões tardias indiscriminadamente pelos 600 s e chamar isso taxa temporal do último segundo.

Implementação local da 008 (2026-10-17): `MetricWindowCollector` serializa registros/rotação, `MetricWindowSink` limita a fila a seis janelas com orçamento calculado de 240 KiB, `MetricWindowTotals` consolida apenas sequência contígua e preserva último gauge. Quality é cumulativa. Callback síncrono `ResponseHeadersObservation` do transporte entrega timestamp/status/Retry-After antes do corpo, independente do terminal; falha do observador é explícita. Dono da operação registra cada evento uma vez; não há deduplicação por request no acumulador.

Schema de janela 1 é independente do plano v2. Codec little-endian de 17.832 bytes, dois histogramas v1, checksum SHA-256 e validação de integridade. Layout e evidências na [FICHA-008](implementation-cards/FICHA-008-METRIC-WINDOWS.md#contrato-concretizado). Encoding/hash e soma de totais fora do lock. Pressão trava novas alterações e sinaliza MetricsPartial; não descartar silenciosamente nem criar fila de observações. Em fronteira já publicada, fechamento é comprovado pelo manifesto sem reescrever a janela. A 009 chama rotação durante geração/drain e conduz parada/flush pela API local; a CLI funcional consome esse contrato na 011.

## Orçamento de memória e CPU

Estimativa didática: concorrência média necessária ≈ R × latência média em segundos. A 1.000 RPS e 100 ms, são cerca de 100 operações simultâneas; a 15.000 RPS e 200 ms, cerca de 3.000. São cálculos, não dimensionamento seguro: caudas, TLS, SO e fila exigem medir margem. Escolher C compatível com RAM/sockets e rejeitar um perfil que exceda envelope calibrado, em vez de aumentar C indefinidamente.

Modelo: `M ≈ runtime + C × (buffer_leitura + estado_async + estruturas_HTTP/TLS) + payload_estático + acumuladores + fila_métricas + margem`. Só o buffer de 16 KiB × C=256 equivale a 4 MiB calculados. O resto depende do runtime e benchmark; não dizer que cada chamada custa apenas 16 KiB. Dois histogramas por acumulador custam 13.344 bytes, além dos contadores e overhead de objetos.

Arrays limitados e enums substituem dicionários com URLs/mensagens arbitrárias. Não manter lista de tasks terminadas, respostas, spans ou eventos individuais. Não registrar log por request nem lançar exceptions intencionalmente para status HTTP de falha. Avaliar alocações por operação, GC, working set, CPU, sockets e lock antes de aplicar pooling adicional ou `ValueTask` indiscriminadamente.

## Confiabilidade e validação

O resultado tem flags independentes `generator_limited`, `clock_uncertain`, `metrics_partial`, `worker_lost` e `target_errors`. `COMPLETED` não significa alvo saudável nem taxa atingida. Drops sempre ativam generator_limited; lateness e recursos complementam o diagnóstico. Uma política de produto poderá definir tolerância para comparar execuções, mas não apagar esses sinais.

Validação por camadas: relógio falso prova agenda e ausência de catch-up; handler falso prova classificação; fixture HTTP real prova streaming, timeout e reuso; teste prolongado prova recursos limitados; teste de CPU reduzida prova que saturação fica visível. O [roadmap](08-DEVELOPMENT-ROADMAP.md) e as [fichas](implementation-cards/README.md) detalham os critérios. Minikube entra somente depois do marco local; não é necessário para validar matemática do motor.

## Rate Limiting — contrato local v2 (2026-10-12)

Recorte autorizado da FICHA-037 sobre a FICHA-001: contratos e validação pura. Na data do recorte puro, transporte, agenda, contadores, histogramas, logs e relatório ainda dependiam das fichas 002–012; estado atual no início deste documento. O plano local passa de schema 1 para 2; não há dados persistidos a migrar. Consumidores futuros devem rejeitar schemas desconhecidos. Não alterar ADR-008: 429/Retry-After são observações, sem alterar RPS, concorrência, agenda ou criar retries.

`RateLimitMode` admite Normal (default), Authorized e Diagnostic. Todos preservam o mesmo perfil e classificam 429. Diagnostic não realiza sondagens. `LoadRetryPolicy` admite somente None; valores desconhecidos são recusados. `HttpSuccessRule` admite somente Completed2xxV1: corpo completo e status 200–299, inclusive 202, sem comprovar processamento de negócio. Não há regra customizável que aceite 429.

A configuração local reutiliza Headers para API key, `Authorization: Bearer ...` e headers personalizados. `TestCredentialHeaderIndex` identifica opcionalmente o header de credencial de teste no array; índice inválido ou valor vazio é recusado. Authorized exige esse índice e `TestAuthorizationDeclared=true`. É declaração local do chamador, não grant, prova de autorização nem indicação de remoção de limites. Outros modos também podem usar credenciais. Valores ficam somente em memória; referências versionadas e cofre pertencem a M3. Todos os headers continuam sujeitos aos limites, colisões sem distinção de caixa e proibições existentes.

`RequestObservation.StatusCode` representa somente status final 200–599. Respostas informativas intermediárias não são observações terminais; 101/upgrade não é suportado no transporte local. `IsRateLimited` deriva exclusivamente de status 429, inclusive após falha de leitura, timeout ou cancelamento; outros 4xx/5xx não ativam esse sinal. A regra de aceitação exige HttpCompleted. `default(RequestObservation)` continua Unknown.

`RetryAfterObservation` é um valor imutável sem texto bruto: Kind = NotObserved (default, captura não realizada), NotApplicable (status diferente de 429), Missing, Invalid, DeltaSeconds ou HttpDate; Delay é nulo nas quatro primeiras categorias. Para datas, ReferenceUtc é o instante civil fornecido pelo chamador ao receber headers, e ClockUncertain=true explicita a dependência de relógio civil sem sincronia comprovada. Datas passadas resultam em zero. Duração normalizada é limitada a 86.400 segundos; WasClamped distingue valores maiores. Parsing puro, sem leitura do relógio, I/O, espera ou alteração do plano.

O parser recebe status, lista de valores brutos e referência UTC: somente 429 é avaliado; lista vazia = Missing, mais de um valor = Invalid (não concatenar/split por vírgula, pois data contém vírgula). Um campo tem no máximo 128 caracteres antes de trim de SP/HTAB. Delta aceita somente dígitos ASCII não negativos; overflow numérico é saturado ao teto, sem perder a verificação de sintaxe. Data aceita IMF-fixdate, rfc850-date e asctime-date conforme [RFC 9110 §5.6.7](https://www.rfc-editor.org/rfc/rfc9110.html#section-5.6.7), com ano >=1900, calendário/dia da semana consistentes e segundo 60 somente em 23:59 UTC (sem consultar tabela externa de segundos intercalares); nenhuma string bruta é retida ou incluída em erros. Header inválido não muda o desfecho HTTP. Um 429 sem observação explícita mantém RetryAfter NotObserved; Missing só resulta de captura efetiva sem campo. Anexar evidência aplicável a status diferente de 429, ou NotApplicable a 429, é erro de contrato. NotObserved não pode ser contabilizado como ausência comprovada do header.

### Integração das métricas — componentes 008–011 implementados

`status_received[429]` conta uma vez nos headers, independentemente de EOF. Não incrementar novamente a distribuição ao consumir a observação terminal. Início, headers e terminal pertencem às suas janelas de registro; preservar as identidades anteriores. `http_success` reutiliza a regra versionada, não criar outro contador de aceitas. Outras respostas finais = soma de status recebidos menos 429; não confundir com falhas de transporte. Os slots 100–199 do array planejado permanecem zero.

Percentual sinalizado = 100 × status_received[429] / soma de status finais recebidos; denominador zero resulta em null. Somar contadores e buckets compatíveis antes de derivar percentuais/quantis. Publicar duração real, drain, população e cobertura; usar intervalo global comum entre workers, sem somar suas durações. Throughput HTTP é http_completed/s; bytes de corpo observados/s é outra medida. Inícios nunca comprovam recebimento pelo servidor.

Retry-After tem contadores fixos por categoria (incluindo captura não realizada), contagem de clamp e de relógio incerto, mínimo/máximo de Delay válido (null sem valores). Registro junto ao status, merge por soma de contagens e min/max dos valores existentes; sem labels por valor, texto bruto ou lista de observações. Acumulador implementado na 008; codec/janela versionados e soma com overflow verificado. 429 sinaliza limitação no caminho HTTP, sem identificar gateway/WAF/proxy/aplicação. Ausência de 429 não prova ausência de limitação; cobertura insuficiente deixa o diagnóstico inconclusivo. Não inferir capacidade real da aplicação enquanto a limitação interferir no teste.

## Consumidor CLI local — 2026-10-17

A CLI 011 usa `LocalRunController.RunAsync` e `ILocalMetricOutput`, confirma escrita/flush das janelas e publica relatório JSON schema 1 independente do plano 2. Projeção explicitamente permitida, sem serializar configuração/plano: método, perfil, limites e políticas, esquema/porta do destino, versões/ambiente. Não publica URL, nomes/valores de headers, payload ou texto arbitrário de exceptions. Logs JSON limitados em stderr; resultado em arquivo dedicado ou stdout. Estado atual, comandos e limites em [README](../README.md); evidências e pendências na [012](implementation-cards/FICHA-012-ENGINE-VALIDATION.md). Registros anteriores sobre host informativo são históricos.
