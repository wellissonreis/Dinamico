# FICHA-011 — CLI local e relatório de resultados

Prioridade: P0. Complexidade: M. Marco: M1. Status: implementada e validada localmente em 2026-10-17; gate de calibração 012 pendente.

## Objetivo

Permitir executar um teste local e ler resultado útil, incluindo limitações do gerador, sem escrever código de teste.

## Contexto arquitetural

Primeiro consumidor funcional do motor, sem frontend web. Relatório JSON será base para persistência M2.

## Conceitos necessários

Resultado parcial não é zero; percentil sempre tem população e aproximação; status do processo comunica término, não saúde do alvo.

## Responsabilidades

Ler configuração validada, aceitar Ctrl+C/SIGTERM, mostrar progresso limitado e produzir relatório JSON final mais janelas em streaming opcional. Expor versões e limites efetivos.

## Fora do escopo

UI React, banco, credenciais SaaS, execução externa arbitrária, exportação de requisições e regressão estatística automática.

## Dependências

FICHAS 009 e 010.

## Contratos

Entrada: arquivo/configuração local com um endpoint loopback e opções públicas; evitar secrets em argumentos. Saída: schema versionado com plano sanitizado, tempos, contadores, histogramas/quantis, status, flags, cobertura e metadados do ambiente. Logs em stderr e resultado em saída dedicada; overwrite de arquivo exige opção explícita. Exit codes propostos: 0 protocolo completo (inclusive HTTP errors), 2 configuração, 3 falha interna/dados parciais, 130 cancelamento.

## Registro histórico da proposta — Rate Limiting v2

CLI/relatório permanecem pendentes; host informativo não executa carga. Seguir o [contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12); evidências do recorte puro estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md).

Consumir SchemaVersion 2 e rejeitar versões desconhecidas. Publicar modo, LoadRetryPolicy.None e HttpSuccessRule.Completed2xxV1 em plano sanitizado; não serializar Headers/configuração/plano completos. Authorized exige índice e declaração local, sem representar grant; Normal/Diagnostic também aceitam credenciais. Conforme [07](../07-SECURITY.md), HTTP com credenciais sintéticas em loopback é permitido no laboratório; o validador não verifica sua origem nem distingue segredos reais de sintéticos. Usar somente segredos sintéticos nesses testes. Exemplo de configuração local no [README](../../README.md#configuração-local-v2--recorte-autorizado).

Relatório futuro distingue planejado/iniciado/http_completed/http_success, status finais, outras respostas finais, percentual 429 e contagens Retry-After/clamp/incerteza/min-max. Apresentar NotObserved como captura não realizada, sem inferir ausência de Retry-After; Missing exige captura efetiva sem campo em 429, e NotApplicable corresponde a outro status. Percentual usa respostas finais recebidas, null sem denominador; min/max null sem Delay válido. Publicar duração real/drain, unidades, populações e cobertura. 202 completo é sucesso HTTP, sem prova de negócio; 429 com falha de corpo mantém sinal e não é completo; 503 com Retry-After não é classificado automaticamente como Rate Limiting. Ausência de 429 não prova ausência de limites, e cobertura insuficiente torna diagnóstico inconclusivo. Não inferir capacidade real da aplicação sob interferência.

Testes futuros: JSON/saída humana com casos acima, fronteiras/drain, cobertura parcial, null e canários de credenciais/header bruto ausentes. NAT/egress e intervalo global entre workers pertencem somente a M4/M5; UI/SSE a M6. Nenhum teste foi executado aqui.

## Decisões técnicas

Host .NET simples e System.Text.Json; parser básico ou biblioteca somente se complexidade justificar. Sink grava janelas incrementalmente com buffer limitado; total final é acumulado em tamanho fixo, não lista de requests/janelas em RAM.

## Passos de implementação

1. Conectar parsing/validação ao host do motor.
2. Integrar sinais e tratamento de erro de saída.
3. Definir relatório com populações e unidades explícitas.
4. Implementar arquivo temporário e conclusão atômica quando possível; resultado incompleto identificado se houver crash.
5. Adicionar exemplos de uso loopback e interpretação.

## Testes obrigatórios

Execução real na fixture, JSON parseável, null para população vazia, overflow visível, cancelamento com código correto e 500 com protocolo concluído. Falha de disco/sink não resulta em sucesso nem fila ilimitada. Arquivo existente não é sobrescrito por default; segredo não consta no relatório.

## Observabilidade

Mensagens humanas mostram solicitado/iniciado/concluído, drops, percentis e qualidade. Reportar latência do alvo somente como latência observada pelo cliente, sem diagnóstico causal automático.

## Critérios de aceite

Um operador executa cenário documentado e entende se a carga pedida foi alcançada. Relatório tem totais conciliáveis, versão/schema e dados suficientes para repetir configuração não sensível.

## Riscos e trade-offs

Saída lenta pode interromper teste pela política de backpressure. Snapshot sanitizado não recupera credenciais e não promete reprodução byte a byte de segredos excluídos.

## Definição de pronto

- [x] Fluxo CLI→fixture→relatório verificado.
- [x] Exit codes, sinais, I/O de arquivo e redaction testados.
- [x] Guia de execução não exige Kubernetes ou banco.

## Entrega atual — 2026-10-17

`LocalCli` e `JsonReportOutput` substituem o host informativo. Uso real, na raiz após build Release:

```sh
dotnet src/Dinamico.LoadEngine.Local/bin/Release/net10.0/Dinamico.LoadEngine.Local.dll run --config examples/local-http.json --output report.json 2>run.jsonl
```

Configuração segura e interpretação completa estão no [README](../../README.md). Envelope obrigatório schemaVersion 2, enums string, campos desconhecidos/duplicados rejeitados (inclusive headers); arquivo <=2 MiB. Continua limitado a literal loopback, sem bypass TLS, retries ou destinos externos. Authorized/Normal/Diagnostic preservam a agenda.

Relatório schema 1 publica projeção allowlist (sem URL/header/payload), ambiente, tempos, janelas incrementais, totais do prefixo confirmado, status nos headers separados dos terminais, Retry-After normalizado e cobertura/flush. Dois histogramas com N/unidades/buckets/quantis aproximados e overflow explícito; null para populações vazias. Taxas derivadas têm denominadores nomeados e escopo observado. Não infere sucesso de negócio, ausência de limites ou capacidade do alvo.

Arquivo dedicado é temporário `.incomplete-<id>` e rename final; overwrite somente explícito. Arquivo de configuração não pode ser o destino. Em falha/crash, temporário/prefixo não comprova resultado completo. Flush não é promessa de fsync nem atomicidade em todo filesystem. Criação/finalização possuem deadlines; escrita do stdout usa handle direto porque ConsoleStream oculta EPIPE. Teste identificou retorno 0 com pipe fechado e a correção foi revalidada com código 3. Saída bloqueada é testada com pipe Linux reduzido a 4 KiB; o host não aguarda I/O não cooperativo indefinidamente nem disputa dispose com writer ativo.

Ctrl+C e SIGTERM Linux param a geração e cancelam I/O; cancelamento com relatório publicado retorna 130. Código 0 significa dados completos/protocolo concluído, inclusive HTTP 500/drops/timeout de request; 2 = configuração/conflito de saída; 3 = falha interna, saída ou dados parciais. Erros usam JSON enum-safe limitado em stderr; nenhuma mensagem de exception é publicada.

Evidência em `CliProcessTests`: subprocesso/fixture real 202/429/503 nos três modos; corpo limitado 200/429; timeout após headers 429 com Retry-After duplicado; canários; conciliação de recebimentos/inícios/terminais; concorrência limitada; null; SIGINT/SIGTERM; arquivo existente/overwrite; configuração rejeitada; diretório ausente; pipe fechado/bloqueado; erros de configuração com 128 headers inválidos e linha limitada. Os testes CLI executam sem paralelismo com outras classes para não usar competição de CPU da suíte como condição dos vetores funcionais. Overflow matemático tem vetores na 007; não foi provocado por espera real >67 s na CLI. Validação consolidada na [012](FICHA-012-ENGINE-VALIDATION.md).
