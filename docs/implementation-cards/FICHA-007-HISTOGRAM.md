# FICHA-007 — Histograma limitado e percentis combináveis

Prioridade: P0. Complexidade: M. Marco: M1. Status: implementada e validada em 2026-10-17.

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

### Decisão de implementação — 2026-10-17

O contrato 03 prevalece sobre o resumo da ficha: incluir soma exata e mínimo/máximo observados. Durações, soma e contagens usam UInt64 em nanossegundos; overflow lança erro antes de alterar o estado. Não há saturação, conversão implícita de TimeSpan ou retenção de amostras. API matemática sem sincronização; lock/rotação pertencem à 008. Merge puro retorna nova distribuição.

A tabela é materializada com ceil exato por comparação de inteiros (`u_i^32 >= 1000^32 × 2^i`), sem Math.Pow no runtime. Schema numérico 1 identifica exclusivamente `log2-32-v1`; qualquer outro é rejeitado. Rank usa UInt128 para ceil(percentual × N / 100), evitando overflow intermediário de UInt64.

Codec v1: tamanho fixo 6.760 bytes, little-endian. Magic ASCII `DNHIST01` (8 bytes), versão UInt32 (offset 8), schema UInt32 (12), número de buckets UInt32 (16), reservado zero UInt32 (20), N/soma/min/max UInt64 (24/32/40/48), 834 contadores UInt64 (56; 6.672 bytes), SHA-256 dos primeiros 6.728 bytes (32 bytes finais). Vazio codifica N/soma/min/max como zero; API retorna min/max e quantis null. Checksum detecta corrupção acidental, não autentica origem. Decoder exige tamanho exato, versão/schema/reservado válidos, checksum, soma de contagens sem overflow, extremos coerentes e soma factível para os buckets/extremos. Falhas de integridade lançam InvalidDataException; consumidores futuros devem mapeá-las para protocol_integrity, nunca retornar vazio. Não há consumidores implementados nesta ficha.

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

Arredondamento e endian incompatíveis quebram merge. Resolução ~2,19% mantida conforme contrato autorizado; vetores verificam a aproximação. Se o requisito mudar, rever ADR antes de implementar consumidores.

## Definição de pronto

- [x] Vetores determinísticos e codec verificados.
- [x] Quantis globais testados com populações de tamanhos diferentes.
- [x] Memória fixa e limites de erro documentados.

## Entrega e validação — 2026-10-17

Arquivos: `src/Dinamico.LoadEngine/Metrics/LogHistogramSchema.cs`, `LogHistogram.cs`, `HistogramCodec.cs` e `tests/Dinamico.LoadEngine.Tests/HistogramTests.cs`. Registro/consulta não alocam por observação (teste com 10.000 registros); cada instância mantém 834 UInt64, além de metadados/overhead. Tabela compartilhada: 833 UInt64. Codec limita entradas a 6.760 bytes e não aloca conforme tamanho declarado por remetente. Não houve benchmark de throughput.

17 casos direcionados: prova inteira de todos os limites, 2.499 observações de limites/vizinhos mais zero/submicrossegundo/60 s/UInt64 máximo, vazio, idênticos, bimodal, cauda rara, distribuição ordenada de referência e erro, merge associativo/comutativo com 9.900×10 ms e 100×1 s, overflow de registro/merge sem mutação, ranks perto de UInt64 máximo, vetor golden do codec, round-trip, todas as truncagens e corrupção em cada byte, headers/schema desconhecidos e inconsistências semânticas com checksum recalculado.

Comandos efetivamente executados (Windows via WSL, SDK 10.0.204):

```sh
dotnet.exe restore Dinamico.slnx --locked-mode -p:RestoreFallbackFolders= -p:RestoreAdditionalProjectFallbackFolders=
dotnet.exe test tests/Dinamico.LoadEngine.Tests/Dinamico.LoadEngine.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~HistogramTests --logger trx --results-directory TestResults/Ficha007/ValidatedDirected
dotnet.exe build Dinamico.slnx --configuration Release --no-restore
dotnet.exe test Dinamico.slnx --configuration Release --no-build --no-restore --logger trx --results-directory TestResults/Ficha007/ValidatedSuite
git diff --check
```

Resultados finais: restore locked aprovado, lockfile inalterado; **17/17 direcionados**, **472/472 na suíte completa**, zero falhas/ignorados; build com **zero avisos/erros**; diff sem erros de whitespace. TRX: `TestResults/Ficha007/ValidatedDirected/welli_WELL-PC_2026-10-17_17_25_34.trx` e `TestResults/Ficha007/ValidatedSuite/welli_WELL-PC_2026-10-17_17_25_38.trx`. Logs em `/tmp/dinamico-ficha007-restore.log` e `/tmp/dinamico-ficha007-validated-{directed,build,suite}.log`.

Ocorrências preservadas: primeiro direcionado passou 14 testes; a tentativa seguinte sem restore falhou com MSB4018 por fallback NuGet inexistente do Visual Studio. A suíte `--no-build` dessa tentativa passou 469 testes usando binário anterior e **não é evidência final**. Restore locked sem fallback inválido corrigiu os assets gerados; nenhuma configuração do projeto/dependência foi alterada. Evidência anterior `TestResults/Continuacao/continuacao.trx` (455, 13/10) continua histórica.

Próximo passo: FICHA-008, com evento explícito de recebimento de headers no transporte para atribuir status/Retry-After à janela de registro correta, separado da conclusão terminal. Integrar os dois histogramas, contadores, lock/rotação e sink limitado somente nessa ficha. Não foram implementadas 008–012; transporte 003 continua parcial, CLI informativa, M1 e FICHA-037 incompletos. Nenhum commit criado; alterações anteriores preservadas.
