# ADR-005 — Agregação limitada e histograma logarítmico fixo

Status: Proposto. Data: 2026-10-08.

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
