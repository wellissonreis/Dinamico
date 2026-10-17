# FICHA-010 — Logs estruturados e amostras limitadas

Prioridade: P0. Complexidade: P. Marco: M1. Status: implementada e validada localmente em 2026-10-17; calibração/aceite M1 continuam na 012.

## Objetivo

Permitir explicar execução e falhas locais com volume limitado e sem expor configuração sensível.

## Contexto arquitetural

Aplica a política de [06](../06-OBSERVABILITY.md) ao ciclo local. Logs não substituem as janelas da 008.

## Conceitos necessários

Evento de mudança de condição difere de log por request. Cardinalidade cresce quando mensagem/URL vira chave; amostragem reduz exemplos, não contadores.

## Responsabilidades

Esquema JSON padrão, eventos de lifecycle/saturação, IDs, allowlist de campos e coletor limitado de exemplos de erro. Resumos operacionais de processo em frequência fixa.

## Fora do escopo

Coletores externos, dashboards, tracing por request, corpos/headers em logs e debugging interativo como requisito.

## Dependências

FICHA-009.

## Contratos

Logs com timestamp, level, event, execution_id, versão e reason_code. Amostra ≤512 bytes, até 5/janela e 100/execução local. Eventos repetidos até um por fingerprint/30 s, com contagem de suprimidos; linha ≤4 KiB. Métricas completas continuam contabilizadas mesmo sem amostra.

## Registro histórico da proposta — Rate Limiting v2

Logs/amostras permanecem pendentes. Aplicar o [contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12); evidências dos contratos/parser serão registradas pelo coordenador em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md).

Allowlist pode publicar modo, retry None, regra Completed2xxV1 e categorias normalizadas de Retry-After. NotObserved significa captura não realizada, sem comprovar ausência do header; Missing exige captura efetiva sem campo em 429, e NotApplicable corresponde a outro status. Nunca registrar configuração/plano completos, valores de Headers, texto bruto de Retry-After ou exceptions arbitrárias. Todos os headers são sensíveis, mesmo nomes personalizados. Um 429 ou 503 não exige log por request; preservar limites de amostragem, cardinalidade e backpressure.

Testar canários em Authorization Bearer, API key, header personalizado, payload, URL, Retry-After inválido e mensagem de exception, nos três modos e sob tempestade de 429/503. Canários devem estar ausentes de erros, logs e amostras; o teste de relatório pertence também à 011, SSE/UI somente a M6. Não alterar contadores ao suprimir logs. Testes futuros, sem execução nesta atualização.

## Decisões técnicas

Logging estruturado disponível no ecossistema .NET, sem framework adicional obrigatório. Campos permitidos por construção, evitando depender de regex para limpar arbitrary exception text. Fila/saída lenta tem limite e não trava geração indefinidamente.

## Passos de implementação

1. Fixar schema/eventos e mapear taxonomia para desfechos.
2. Implementar amostragem por categoria limitada.
3. Adicionar transições de saturação e resumos de processo.
4. Aplicar rate limit/tamanho e contabilizar supressão.
5. Testar informações sensíveis por canários.

## Testes obrigatórios

100% de falhas por muitos eventos mantém limites; segredo sintético em URL/header/body/exception não aparece; status 500 não cria exception/log por request; categoria rara não é totalmente escondida pela tempestade. Saída de log lenta não cria fila ilimitada.

## Observabilidade

O próprio mecanismo informa eventos suprimidos/amostras descartadas e versão do schema. Ausência de suporte a métrica de runtime é unknown.

## Critérios de aceite

É possível explicar um cancelamento, timeout e saturação só pelo relatório/eventos. Nenhum canário vaza; memória/volume permanecem limitados sob falhas totais.

## Riscos e trade-offs

Amostragem não permite reconstruir cada requisição. Mensagens de bibliotecas podem conter URL, exigindo controlar categorias de logging automáticas.

## Definição de pronto

- [x] Limites de amostra/log comprovados.
- [x] Testes de redaction e backpressure aprovados.
- [x] Logs são separados do JSON de resultado da CLI.

## Entrega atual — 2026-10-17

`Observability/BoundedRunObservability.cs` consome somente modo, janelas imutáveis e resultado seguro; não recebe configuração, URL, headers, payload ou exceptions. A CLI chama Start, ObserveWindow no ACK da saída e Finish. Transições do controlador são publicadas no encerramento; não há callback de logging no hot path. Resumo operacional a cada janela, inclusive final parcial, com CPU acumulada, RSS e heap; medições não disponíveis ficam nulas. Alocações, pausas GC, sockets/handler e throttling não foram instrumentados.

Pump independente com fila de 32 linhas; produtores não aguardam stderr. Encerramento da CLI espera até 1 s; timeout/falha ficam no snapshot do relatório. Linhas <=4 KiB e amostras <=512 bytes por construção/teste; até 5 por janela/100 por execução. Fingerprint categoria/status tem intervalo de 30 s e contador de suprimidos. Categorias terminais precedem HTTP, preservando categoria rara sob tempestade 429/503. Acima do orçamento, os descartes são explícitos. Erros de transporte são uma categoria agregada (não há exemplos com fase DNS/TLS). Amostras ficam em stderr; relatório contém estatísticas da observabilidade. Métricas não são reduzidas por amostragem.

Evidência: `BoundedObservabilityTests` verifica três modos, 125 janelas com 600 respostas HTTP e uma falha rara/janela, limites, deduplicação, writer bloqueado ignorando token e exception com canário. `CliProcessTests` injeta canários em bearer/API key/header personalizado/path/query/payload/Retry-After inválido e verifica stderr/relatório, incluindo JSON de erro. Rodada integrada e artefatos detalhados na [012](FICHA-012-ENGINE-VALIDATION.md). Não é ensaio prolongado de capacidade.
