# ADR-005 — Agregação limitada e histograma logarítmico fixo

Status atual: histograma implementado na FICHA-007 (2026-10-17); janelas/lock implementados na 008; integração ao ciclo local implementada na 009; observabilidade/relatório/gate pendentes. Registro original: Proposto, 2026-10-08.

## Contexto

Percentis globais precisam ser combináveis e o uso de RAM não pode crescer por requisição.

## Problema

Oferecer P50/P95/P99 úteis com limites claros, sem escolher um pacote adicional sem validação.

## Alternativas consideradas

Observações individuais crescem com RPS; média de percentis é matematicamente inválida; bins lineares perdem resolução; t-digest e HDR exigem implementação/pacote e avaliação próprios. HDR permanece opção se for necessária maior precisão.

## Decisão

Janelas imutáveis de 5 s; dois histogramas `log2-32-v1` de 834 buckets cada, conforme fórmula e populações de 03. Contadores exatos, quantis aproximados pelo limite superior, overflow explícito, schema versionado. Lock curto inicialmente; amostras de erros limitadas.

## Justificativa

Memória e serialização previsíveis, merge por soma e limite de erro auditável de aproximadamente 2,19% acima de 1 µs. Não reproduzir a complexidade interna do HDR.

## Consequências positivas

Sem retenção por request; percentis globais coerentes; teste determinístico simples.

## Consequências negativas

Quantização, payload denso e contenção do acumulador precisam ser aceitos/medidos. Não recuperar distribuição exata ou amostras descartadas.

## Condições para revisar a decisão

Erro de percentil não atende usuários, formato domina armazenamento ou lock limita geração. Comparar HDR validado, compressão ou shards com mesmos vetores de teste; nunca mesclar schemas incompatíveis.

## Registro de implementação — 2026-10-17

A FICHA-007 materializa somente o componente matemático: tabela canônica exata, memória fixa, quantis por rank e merge puro, soma/min/max exatos em nanossegundos. UInt64 com overflow verificado; rank e validação de somas possíveis usam UInt128 intermediário. Codec v1 little-endian fixo com schema explícito, SHA-256 e validação semântica; detalhes normativos em [03](../03-LOAD-ENGINE-DESIGN.md#histograma-e-p50p95p99) e na [ficha](../implementation-cards/FICHA-007-HISTOGRAM.md). Não altera a resolução aceita; os vetores determinísticos verificam o limite de erro. Janelas de 5 s, dois histogramas por acumulador, lock e rotação permanecem integração futura da 008.

## Continuação — janelas locais, 2026-10-17

A 008 implementa lock, rotação monotônica, snapshots imutáveis, dois histogramas, sink limitado e consolidação local contígua. Codec/schema de janela próprios; evidências na [FICHA-008](../implementation-cards/FICHA-008-METRIC-WINDOWS.md). O registro anterior da 007 permanece histórico. Contenção ainda não medida; controlador 009, observabilidade/relatório e gate 012 pendentes.

## Continuação — ciclo local, 2026-10-17

A 009 integra o acumulador durante geração/drain, retém janelas até confirmação da saída (máximo seis sem ACK) e limita flush a 10 s com grace de cancelamento. Qualidade, completude dos dados e razão de término são distintas. O codec/histograma não mudou; evidências na [FICHA-009](../implementation-cards/FICHA-009-LOCAL-LIFECYCLE.md). Registros anteriores permanecem históricos; não há aceite de M1 ou medição de contenção.
