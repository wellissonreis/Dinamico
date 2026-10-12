# FICHA-010 — Logs estruturados e amostras limitadas

Prioridade: P0. Complexidade: P. Marco: M1. Status: proposta de integração futura; não implementada.

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

## Integração futura — Rate Limiting v2

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

- [ ] Limites de amostra/log comprovados.
- [ ] Testes de redaction e backpressure aprovados.
- [ ] Logs são separados do JSON de resultado da CLI.
