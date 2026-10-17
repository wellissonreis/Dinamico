# FICHA-008 — Contadores e janelas imutáveis

Prioridade: P0. Complexidade: M. Marco: M1. Status: concluída e validada em 2026-10-17 (componente local; integração ao ciclo na 009).

## Objetivo

Transformar observações concorrentes em janelas completas e limitadas, com contagens conciliáveis e populações de latência corretas.

## Contexto arquitetural

É o núcleo da telemetria local que será transportada ao CP. Não implementa ingestão PostgreSQL ou retries de rede.

## Conceitos necessários

Delta por janela difere de snapshot cumulativo. Operação pode começar numa janela e terminar em outra. Gauge não é contador; ausência de janela não equivale a zero.

## Responsabilidades

Contabilizar eventos no instante de registro serializado definido em 03, girar janela de 5 s, manter dois histogramas, fechar janela parcial e gerar sequência/hash canônicos. Duração do I/O mantém marcos próprios; medir atraso até registro. Fornecer sink limitado para consumo local.

## Fora do escopo

Percentil por URL/usuário, métricas por request persistidas, SQL, comunicação com CP e detalhamento de logs.

## Dependências

FICHAS 006 e 007.

## Contratos

`MetricWindow`: schema, sequence, window_index, start/end relativos, contadores, status[100..599,other], dois histogramas e flags. Zero é emitido quando houve janela vazia real. Sink aceita janela imutável ou sinaliza falta de capacidade. Teto 6 janelas/256 KiB, incluindo serialização prevista e amostras futuras.

## Integração implementada — Rate Limiting v2

Acumulador/janelas implementados nesta continuação; o parser continua puro e a integração ao ciclo local permanece na 009. Seguir o [contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12); evidências do recorte estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md).

Registrar status_received[429] e Retry-After uma vez nos headers, independentemente de EOF; não repetir status no terminal. Início, headers e terminal pertencem às respectivas janelas de registro, preservando as identidades de 03. Slots 100–199 ficam zero; finais são 200–599. http_success reutiliza Completed2xxV1; 429 nunca é sucesso, e 2xx incompleto também não. Outras respostas finais = soma(status_received) menos 429, sem confundir com falha de transporte.

Acumulador fixo: contagens por categoria Retry-After (NotObserved, NotApplicable, Missing, Invalid, DeltaSeconds, HttpDate), clamp e relógio incerto; min/max de Delay válido, null sem valores. NotObserved indica captura não realizada e não entra em Missing; Missing exige captura efetiva sem campo em 429, e NotApplicable corresponde a outro status. Sem labels por valor, texto bruto ou lista de observações. Versionar codec/janela explicitamente; somar com overflow verificado e validar compatibilidade antes de merge. O SchemaVersion 2 do plano não implementa esse codec.

Derivar percentual = 100 × status_received[429] / soma dos status finais; zero denominador = null. Somar contadores/buckets compatíveis antes de percentuais/quantis. Publicar duração real, drain, populações e cobertura; throughput HTTP = http_completed/s, bytes observados/s é outra medida. Em M5, usar intervalo global comum sem somar durações dos workers.

Testes executados: 429 que termina em outra janela com timeout (um status, um terminal, sem HTTP completo); 503 com Retry-After sem contagem de limitação; 202 completo e 2xx incompleto; categorias/clamp/incerteza/min-max; janela vazia, última parcial, drain, overflow, schema incompatível e cobertura ausente diferente de zero. Replay/dedup distribuído depende de M2/M5. Resultados reais desta continuação abaixo.

## Decisões técnicas

Lock curto de atualização/rotação, sem I/O dentro dele; array novo/reciclado só quando ownership for inequívoco. Não misturar acumulador ainda mutável com envelope já enviado.

## Passos de implementação

1. Mapear categorias e populações conforme 03.
2. Associar cada evento à janela do registro sob lock e garantir rotação sem perda ou reabertura de janela fechada.
3. Implementar snapshot fechado e último intervalo parcial.
4. Somar totais finais sem média de percentis.
5. Sinalizar saturação do sink ao controlador da execução.

## Testes obrigatórios

Conclusão atravessa fronteira; rotação concorre com requests; janela vazia; drain gera janela posterior; soma de buckets/contadores preservada; gauges não somados no tempo. Consumidor parado atinge limite previsível e não expande buffer. Hash idêntico para o mesmo conteúdo serializado.

## Observabilidade

Janelas emitidas, capacidade/ocupação do sink, conflito de schema e inconsistência de contagem. Não logar histograma inteiro.

## Critérios de aceite

Invariantes finais de 03 fecham sem dupla contagem. Cada janela tem dono único após fechamento. Pipeline não retém amostras individuais e pressão de saída é explícita.

## Riscos e trade-offs

Lock pode limitar throughput e exige medição; otimizar com shards somente se confirmado. Rotação baseada em eventos atrasados precisa atribuição determinística, não relógio UTC mutável.

## Definição de pronto

- [x] Corridas de rotação/incremento aprovadas.
- [x] Última janela e janelas vazias fazem parte do contrato.
- [x] Memória/sink limitados e erro de pressão observável.

## Decisões da continuação — 2026-10-17 (antes da implementação)

Implementar a 008 como componente local, sem antecipar o controlador 009. Um callback síncrono de headers do transporte entrega status/Retry-After e timestamp monotônico antes da leitura do corpo; falha do observador é explícita, não sucesso HTTP. O chamador registra início antes de enviar e terminal exatamente uma vez; o acumulador não guarda histórico por operação nem tenta deduplicação distribuída.

Schema de janela independente do plano: versão 1, histogramas `log2-32-v1`. Intervalos de 5 s, sequence = window_index (início zero), atribuição no relógio monotônico dentro do lock. APIs de rotação explícita permitem ao controlador emitir vazias durante geração/drain. Encerramento em fronteira não cria intervalo zero extra; encerramento em T0 emite uma janela parcial vazia, comprovando término. Manifesto distingue cobertura completa de pressão/ausência; gauge in_flight não se soma no tempo. Merge de totais exige índices contíguos, soma deltas/histogramas e conserva somente o último gauge.

Sink local com no máximo seis referências a janelas imutáveis e reserva conservadora de 40 KiB por slot (240 KiB), incluindo arrays, frame serializado e folga para metadados/amostras futuras. Sem espera/I/O no lock; saturação lança erro, trava novas alterações e informa MetricsPartial ao controlador futuro. Codec canônico little-endian de tamanho fixo, checksum SHA-256, schemas e invariantes validados; encoding/hash fora do lock. Não há flush remoto, SQL ou replay/dedup nesta ficha.

## Contrato concretizado

`MetricWindowCollector` registra admissão e drops (cada um incrementa due atomicamente), headers e terminais; `RecordTerminal` nunca incrementa status. `ResponseHeadersObservation` inclui timestamp do relógio do transporte; `SendAsync(index, token, observeHeaders)` notifica antes do corpo. O callback deve ser rápido; falhas escapam como `TransportObserverException`, preservando a causa para o controlador. A sobrecarga anterior de SendAsync permanece compatível. Para terminais/drops/inícios, o chamador fornece o timestamp de observação do mesmo relógio. A conversão explícita de TimeSpan para nanossegundos multiplica ticks por 100 com overflow verificado; a classificação dos buckets ocorre fora do lock.

Janelas fechadas expõem somente metadados/arrays imutáveis e `HistogramSnapshot`, sem API de mutação. Quality é cumulativa dentro da execução. `MetricWindowTotals.Append` exige sequência desde zero, sem lacunas/replay/reordenação, soma deltas e distribuições, mantém último gauge e verifica identidade acumulada de in-flight. O manifesto comprova intervalo final e cobertura; não é confirmação de flush nem sucesso da execução. Nenhum observador é deduplicado por índice no acumulador: o dono de cada operação deve registrar início/headers/terminal uma vez. Um terminal sem headers não sintetiza um status.

`Advance` deve ser chamado periodicamente, inclusive no drain, pelo controlador 009. `Close` é idempotente; em fronteira já publicada, encerra por manifesto sem alterar a janela anterior nem emitir duração zero extra. Em T0 há uma janela parcial vazia explícita. Ausência de manifesto completo, falta de índice e saturação são distintas de zero. Após pressão/overflow, o acumulador trava alterações e publica diagnóstico parcial; o controlador deve parar e preservar o prefixo comprovado.

Codec `MetricWindowCodec`: exatamente 17.832 bytes little-endian. Cabeçalho de 120 bytes: magic `DNWIN001` (0), schema UInt32 1 (8), histograma UInt32 1 (12), index/sequence UInt64 (16/24), start/end em ticks de 100 ns UInt64 (32/40), in_flight UInt64 (48), Quality UInt32 (56), final UInt32 0/1 (60), registros/soma/máximo do atraso em ns UInt64 (64/72/80), Retry-After clamp/incerteza UInt64 (88/96), min/max do Delay em ticks UInt64 (104/112; UInt64.MaxValue significa null). Seguem 13 contadores UInt64 na ordem de `MetricCounter` (120), 501 status (224), seis categorias RetryAfterKind (4232), dois frames HistogramCodec v1 (4280/11040), SHA-256 dos primeiros 17.800 bytes (17800). Hash é do conteúdo anterior ao checksum, sem autenticação de origem. Decoder rejeita tamanho, schema, campos, populações e contagens incoerentes, incluindo overflow; nunca devolve vazio em erro.

Sink: fila previamente dimensionada, no máximo seis janelas; reserva de 40 KiB por slot/240 KiB total. Uma janela retida mais sua representação serializada cabe nessa reserva, com folga estrutural; é orçamento calculado, não medição de RSS. Acumulador ativo e buffers transitórios de encoding são separados da fila. Não há amostras nesta ficha; adicioná-las na 010 exige respeitar a reserva existente. Encoding/hash/merge de totais ocorrem fora do lock do acumulador. O consumidor é responsável por não reter janelas indefinidamente após leitura.

## Validação real — 2026-10-17

Arquivos principais: `Metrics/MetricWindow.cs`, `MetricWindowCollector.cs`, `MetricWindowCodec.cs`, `MetricWindowTotals.cs`, `Transport/ResponseHeadersObservation.cs`, extensão pontual de `LocalHttpTransport.cs` e preflight/classificação interna em `LogHistogram.cs`. Testes em `MetricWindowTests.cs`.

24 casos novos: fronteiras/T0/vazias/parcial/drain, status e timeout em janelas distintas com timer controlado no transporte, corpo bloqueado e callback antes de EOF, observador falho, seis desfechos/populações/bytes, Retry-After/clamp/incerteza/extremos, 800 operações concorrentes com rotação, contagem de drops/gauge, atraso de registro separado da duração I/O, sink parado, overflow e relógio regressivo, lacunas/replay/cobertura, codec/hash/endian/truncagens/corrupção/schema/invariantes e soma de totais sem mutação em overflow.

```sh
dotnet.exe restore Dinamico.slnx --locked-mode -p:RestoreFallbackFolders= -p:RestoreAdditionalProjectFallbackFolders=
dotnet.exe test tests/Dinamico.LoadEngine.Tests/Dinamico.LoadEngine.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~MetricWindowTests|FullyQualifiedName~HistogramTests|FullyQualifiedName~LocalHttpTransportTests|FullyQualifiedName~TimingDeadlineTests' --logger trx --results-directory TestResults/Ficha008/ValidatedDirected
dotnet.exe build Dinamico.slnx --configuration Release --no-restore
dotnet.exe test Dinamico.slnx --configuration Release --no-build --no-restore --logger trx --results-directory TestResults/Ficha008/ValidatedSuite
git diff --check
```

Restore locked aprovado, lockfile inalterado. Direcionados: **87/87**; suíte completa: **496/496**, zero falhas/ignorados; build Release: zero avisos/erros. TRX: `TestResults/Ficha008/ValidatedDirected/welli_WELL-PC_2026-10-17_17_39_09.trx` e `TestResults/Ficha008/ValidatedSuite/welli_WELL-PC_2026-10-17_17_39_18.trx`. Logs: `/tmp/dinamico-ficha008-{restore,validated-directed,validated-build,validated-suite}.log`. A primeira rodada teve 84 aprovados/1 falha por asserção que esperava ArgumentException em vez de ArgumentOutOfRangeException; corrigida a asserção e ampliado teste do deadline. Rodada intermediária: 86 aprovados. A revisão final acrescentou rejeição de gauge inicial impossível, clamp/extremos/flags incoerentes com checksum válido; resultados finais recompilados acima. Os 472 testes da 007 são evidência anterior, preservada.

Não houve benchmark de contenção/RSS, flush em disco, controlador, relatório ou execução remota. Próxima ficha: 009 para integrar agenda, admissão, eventos e rotação/drain, tratar exceção de pressão/observador e confirmar flush local com prazo. Transporte 003 ainda parcial; 009–012, M1 e FICHA-037 permanecem pendentes. Nenhum commit criado; alterações preexistentes preservadas. Seis janelas tmux temporárias já ociosas desta sessão foram fechadas após verificar ausência de processos filhos; sessões e processos do usuário foram mantidos.
