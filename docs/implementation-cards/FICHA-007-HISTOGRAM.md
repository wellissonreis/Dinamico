# FICHA-007 — Histograma limitado e percentis combináveis

Prioridade: P0. Complexidade: M. Marco: M1. Status: proposta.

## Objetivo

Obter percentis aproximados com erro conhecido e mesclar distribuições sem manter todas as durações.

## Contexto arquitetural

Componente matemático reutilizado no worker e na consolidação futura. Schema canônico em [03](../03-LOAD-ENGINE-DESIGN.md), decisão no ADR-005.

## Conceitos necessários

Quantil por rank, limites de buckets, quantização, população vazia e overflow. Média de P95 de workers não é P95 global.

## Responsabilidades

Gravar valores, contar/somar, consultar P50/P95/P99, mesclar somente schemas compatíveis e serializar com formato limitado/versionado.

## Fora do escopo

Biblioteca HDR própria, captura de requisições, locks do acumulador, banco e percentis exatos de amostras descartadas.

## Dependências

FICHA-001.

## Contratos

`log2-32-v1`: limites superiores canônicos em nanossegundos, i=0..832, mais overflow, total 834 contadores UInt64; especificar endianness no formato. Quantil devolve N, limite superior/overflow ou null. Schema/tamanho incompatível é erro, sem conversão silenciosa.

## Decisões técnicas

Array fixo e busca em limites para classificação inicial. Tabela única versionada evita diferenças de arredondamento entre runtime/CP. Rank ceil(p×N) com aritmética verificada; soma de contagens detecta overflow.

## Passos de implementação

1. Materializar e validar tabela monotônica de limites.
2. Implementar registro, consulta e merge puro.
3. Definir codec com tamanho fixo, schema e endianness.
4. Cobrir valores nas fronteiras e overflow.
5. Medir custo apenas após correção comprovada.

## Testes obrigatórios

Zero, submicrossegundo, cada limite e vizinhos, 60 s, acima de 67,108864 s, conjunto vazio, valores idênticos, bimodal e cauda rara. Comparar quantis com vetor ordenado pelo limite de erro de 03. Merge associativo/comutativo em contagens; rejeitar schema diferente, bytes truncados e contagens inválidas/overflow.

## Observabilidade

Contagem de overflow e schema no relatório. Erro de codec vira protocol_integrity; não deve retornar histograma zerado.

## Critérios de aceite

Cada observação incrementa exatamente um bucket; soma de buckets=N. Payload de contadores tem 6.672 bytes por histograma. Quantis respeitam limite declarado, inclusive null/overflow; merge preserva distribuição conhecida.

## Riscos e trade-offs

Arredondamento e endian incompatíveis quebram merge. Resolução ~2,19% é proposta de produto; se rejeitada, rever ADR antes de implementar consumidores.

## Definição de pronto

- [ ] Vetores determinísticos e codec verificados.
- [ ] Quantis globais testados com populações de tamanhos diferentes.
- [ ] Memória fixa e limites de erro documentados.
