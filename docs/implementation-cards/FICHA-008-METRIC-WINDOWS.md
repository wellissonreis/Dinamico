# FICHA-008 — Contadores e janelas imutáveis

Prioridade: P0. Complexidade: M. Marco: M1. Status: proposta de integração futura; não implementada.

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

## Integração futura — Rate Limiting v2

Agregador/janelas permanecem pendentes; não fazem parte do parser puro atual. Seguir o [contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12); evidências do recorte estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md).

Registrar status_received[429] e Retry-After uma vez nos headers, independentemente de EOF; não repetir status no terminal. Início, headers e terminal pertencem às respectivas janelas de registro, preservando as identidades de 03. Slots 100–199 ficam zero; finais são 200–599. http_success reutiliza Completed2xxV1; 429 nunca é sucesso, e 2xx incompleto também não. Outras respostas finais = soma(status_received) menos 429, sem confundir com falha de transporte.

Acumulador fixo: contagens por categoria Retry-After (NotObserved, NotApplicable, Missing, Invalid, DeltaSeconds, HttpDate), clamp e relógio incerto; min/max de Delay válido, null sem valores. NotObserved indica captura não realizada e não entra em Missing; Missing exige captura efetiva sem campo em 429, e NotApplicable corresponde a outro status. Sem labels por valor, texto bruto ou lista de observações. Versionar codec/janela explicitamente; somar com overflow verificado e validar compatibilidade antes de merge. O SchemaVersion 2 do plano não implementa esse codec.

Derivar percentual = 100 × status_received[429] / soma dos status finais; zero denominador = null. Somar contadores/buckets compatíveis antes de percentuais/quantis. Publicar duração real, drain, populações e cobertura; throughput HTTP = http_completed/s, bytes observados/s é outra medida. Em M5, usar intervalo global comum sem somar durações dos workers.

Testes futuros: 429 que termina em outra janela com timeout (um status, um terminal, sem HTTP completo); 503 com Retry-After sem contagem de limitação; 202 completo e 2xx incompleto; categorias/clamp/incerteza/min-max; janela vazia, última parcial, drain, overflow, schema incompatível e cobertura ausente diferente de zero. Replay/dedup distribuído depende de M2/M5. Nada disso foi executado nesta atualização.

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

- [ ] Corridas de rotação/incremento aprovadas.
- [ ] Última janela e janelas vazias fazem parte do contrato.
- [ ] Memória/sink limitados e erro de pressão observável.
