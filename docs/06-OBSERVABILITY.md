# 06 — Observabilidade

## Objetivo e limites

Conseguir reconstruir intenção, plano, transições, autorização de tráfego, perda de dados e condições de processo sem logs por requisição. São três produtos diferentes: logs operacionais JSON em stdout, eventos duráveis de execução/auditoria no PostgreSQL e métricas agregadas do teste. Não usar stdout como fonte do relatório histórico.

No início, usar logging estruturado e instrumentação do runtime .NET, sem stack distribuída adicional. Exportação externa poderá ser adicionada se operação exigir; hoje o canal interno autenticado carrega resumos operacionais do worker. Detalhes de SQL, senha, token e URL completa nunca são necessários para explicar um evento.

## Identificadores e esquema JSON

`execution_id` acompanha toda execução; `worker_slot` identifica parcela e `worker_attempt_id` identifica processo autorizado. `pod_uid`, `job_uid`, `plan_version`, `state_version` e `sequence` resolvem ambiguidades de retry. `correlation_id` é um ID gerado/validado em cada comando de API, propagado nos eventos decorrentes. `trace_id`/`span_id`, se disponíveis nas atividades do controle, são complementares; não criar um trace gigante com milhões de spans de carga.

Não enviar IDs internos ou `traceparent` automaticamente ao sistema sob teste. Correlação com o alvo é opt-in futuro e altera a carga/headers, devendo constar no snapshot. Rejeitar caracteres de controle em IDs recebidos e limitar tamanho; valor livre do usuário não vira nome de evento.

Exemplo de início:

```json
{"timestamp":"2026-10-08T18:00:00Z","level":"Information","event":"worker.started","service":"load-worker","service_version":"planned-v1","execution_id":"ex-example","worker_slot":0,"worker_attempt_id":"attempt-example","correlation_id":"cmd-example","plan_version":1,"rps_assigned":100,"concurrency_limit":64,"http_version":"1.1","metric_schema":"log2-32-v1"}
```

Exemplo de saturação com evento resumido, sem uma linha por drop:

```json
{"timestamp":"2026-10-08T18:00:05Z","level":"Warning","event":"generator.capacity_limited","execution_id":"ex-example","worker_slot":0,"worker_attempt_id":"attempt-example","window_index":0,"scheduled_due":500,"started":450,"not_started_concurrency":50,"in_flight_peak":64,"dispatch_lateness_max_ms":3.2,"reason_code":"concurrency_limit"}
```

Exemplo de retry deduplicado:

```json
{"timestamp":"2026-10-08T18:00:06Z","level":"Debug","event":"metrics.duplicate_ack","execution_id":"ex-example","worker_attempt_id":"attempt-example","sequence":0,"payload_hash_match":true,"persisted":true}
```

Os números dos exemplos são ilustrativos. Nenhum exemplo representa benchmark realizado.

## Eventos obrigatórios e severidade

| Evento | Nível | Persistência |
|---|---|---|
| API start/stop, migração/versão, worker start/config validada | Information | stdout; versão no resultado |
| Pedido aceito/negado, autorização de destino, secret usado/revogado | Information/Warning | Auditoria mínima no banco |
| Estado alterado, plano armado, worker registrado/finalizado | Information | Evento transacional no banco |
| Saturação iniciou/cessou, métricas atrasadas, lease próximo de expirar | Warning | Resumo de janela + stdout com rate limit |
| Lease expirou, worker perdido, OOM, finalização incompleta | Error | Evento e flag persistidos |
| Violação de protocolo/hash ou isolamento | Error/Critical conforme impacto | Auditoria e falha fechada |
| Retry normal, heartbeat individual, detalhamento de pool | Debug | Desligado por padrão; prazo e limite de volume |
| Worker stopped | Information/Error | Razão, contagens, flush confirmado e exit code |

Não emitir Warning para cada HTTP 500: contagem e amostra bastam. `Critical` é perda de capacidade de operar com segurança/integridade, não sinônimo de “requisição falhou”. Cancelamento solicitado é comportamento esperado, não exception de nível Error.

## Taxonomia e exceptions

Categorias fechadas: `configuration`, `authorization`, `target_policy`, `dns`, `connect`, `tls`, `request_timeout`, `response_read`, `response_limit`, `http_status`, `scheduler_late`, `concurrency_limit`, `control_unavailable`, `control_lease_expired`, `telemetry_backpressure`, `worker_lost`, `protocol_integrity`, `internal`. Subcódigos também são enumerados; status HTTP é inteiro limitado. A taxonomia de logs é mais ampla que as categorias mutuamente exclusivas dos contadores de 03; documentar o mapeamento no contrato.

Capturar exceptions nas fronteiras de operação/transporte e do host. Erros HTTP esperados viram contadores; erros inesperados viram código, tipo permitido e stack trace sanitizada limitada, por fingerprint da localização do código. Mensagens de exceptions podem conter URLs, userinfo e segredos: não logar `Exception.ToString()` indiscriminadamente. Exceção não observada é bug e gate de reprovação do motor.

## Métricas operacionais e de teste

| Grupo | Medições | Uso |
|---|---|---|
| Motor | In-flight atual/pico, drops por razão, atraso de dispatch, tamanho de fila de métricas | Identificar limitação do gerador |
| Processo/runtime | CPU tempo e limite, RSS/working set, heap, taxa de alocação, GC/pausas e ThreadPool | Detectar contenção ou retenção |
| Transporte | Conexões abertas/criadas, fila do handler quando disponível, bytes de corpo, erros DNS/TLS/connect | Diferenciar pressão de conexão e alvo |
| Kubernetes | CPU throttling quando acessível, OOM/exit, Pending, eviction, recursos e nó | Identificar interferência de infraestrutura |
| CP/banco | Latência/erros de ingestão, pool DB, backlog de reconciliação, leases perdidos, cleanup atrasado, SSE ativos | Validar plano de controle |
| Teste | Planejado/iniciado/concluído, status, falhas, histogramas, P50/P95/P99 e cobertura | Entregar resultado ao usuário |

Medições não disponíveis no ambiente são `unknown`, nunca zero. Instrumentação do handler/runtime deve ser validada na versão exata do .NET; não assumir que todas as fases da rede são expostas. Labels operacionais limitados a serviço, versão, ambiente e categoria. `execution_id` e IDs de usuário não serão labels permanentes de uma série global de monitoramento: pertencem a logs/eventos e séries de teste com retenção. O usuário não fornece labels arbitrários.

Gauges como CPU e in-flight não são somados ao longo do tempo; preservar média ponderada pela duração e máximo quando aplicável. Histogramas e contadores são somáveis, respeitando as populações de 03. Exemplos de gráficos da futura UI: RPS pedido/iniciado, proporção de drops, latência de respostas completas com contagem, timeouts e cobertura por janela.

## Volume e retenção

Um resumo de métricas por 5 s, heartbeat de 2 s sem log de sucesso e eventos apenas em mudanças de condição. Amostras: até 5 por janela, 100 por worker/execução e 512 bytes por amostra. Deduplicar por categoria/código/fase/status, com contador de ocorrências; reservar categorias para que uma tempestade de 500 não esconda um erro TLS. Não usar URL, corpo ou mensagem livre como chave.

Rate limit inicial de eventos repetidos: uma emissão por fingerprint a cada 30 s e contador de suprimidos. Máximo 4 KiB por linha operacional; truncamento é explícito. Eventos de auditoria críticos são gravados transacionalmente, com limites de input, e não descartados pelo rate limit de stdout. Cancelamento deve continuar possível mesmo quando logs estão lentos.

Retenções canônicas em [04](04-DATA-ARCHITECTURE.md): detalhe/amostras 7 dias, resumo/eventos de execução 30, auditoria 90. Rotação de logs do nó não garante arquivo durável. Antes de limpar Jobs, copiar condição de término e eventos relevantes para o banco com limites de volume. Persistir resumos operacionais necessários para explicar uma falha, não todos os logs.

## Roteiro de investigação

1. Conferir snapshot e qualidade: taxa pedida, W, C, timeout, versão, flags e cobertura. Uma janela ausente já limita a conclusão.
2. Comparar due/started/completed e lateness. Drops com alvo rápido e CPU/throttling alto sugerem limitação do gerador.
3. Conferir erros de conexão, churn, sockets/conntrack, bytes/s e retransmissões se disponíveis. Latência pode ser da rede ou fila do cliente.
4. Comparar com métricas do próprio alvo, quando o operador tiver acesso; fila/CPU/DB do alvo e contagem recebida não podem ser inferidas só pelo gerador.
5. Fazer experimento controlado: mesmo alvo com mais workers mantendo RPS; mesmo worker com fixture rápida; alvo em host separado; mudar apenas um limite de cada vez.

Se mais workers reduzem drops e preservam a latência do alvo, há evidência de limitação do gerador. Se aumentar workers não muda throughput e o alvo mostra fila crescente, há evidência do alvo. Se ambos parecem ociosos e há perdas/limite de link, investigar rede. São inferências, não diagnóstico garantido. Workers, alvo e banco no mesmo Minikube competem pelo mesmo hardware e não isolam causalidade.

## Validação

Injetar segredo sintético em URL/header/payload/exception e comprovar ausência em logs, relatório e amostras. Simular 100% erros durante teste prolongado e medir volume limitado. Reiniciar CP, matar worker e reconstruir causa apenas por relatório/eventos. No Minikube, comparar caso de CPU limitada, OOM e alvo lento; `kubectl logs`, `describe` e métricas do processo são ferramentas de inspeção, não requisitos de um backend distribuído de observabilidade.

## Diagnóstico de Rate Limiting — integração por marco

Contratos locais identificam 429 e normalizam Retry-After; emissão de métricas/logs/relatórios ainda depende de 008/010/011. Exibir carga planejada, iniciada, HTTP completo, http_success pela regra publicada, 429, outros status finais, falhas, timeouts, cancelamentos e não iniciadas separadamente. Publicar unidades, população de latência, intervalo e cobertura conforme [03](03-LOAD-ENGINE-DESIGN.md). Não contar novamente status no terminal.

429 é evidência de limitação sinalizada no caminho HTTP; não identifica sozinho o componente que a aplicou. 503 com Retry-After não ativa esse diagnóstico. A falta de 429 não prova ausência de limitação; dados incompletos tornam conclusões limitadas/inconclusivas. Não inferir capacidade real da aplicação enquanto houver interferência da limitação. Respostas rápidas de rejeição podem baixar os percentis; apresentar composição por status.

Relatórios futuros usam projeção por allowlist, nunca configuração/plano completos. Não emitir log por 429 nem reter texto bruto de Retry-After. Canários devem cobrir API keys, bearer e nomes de headers não convencionais em logs, erros, relatórios e SSE quando esses canais existirem. Hoje só são verificáveis os erros estruturados, representações textuais seguras e evidências tipadas.

IPs de saída pertencem a M4/M5: exibir somente inventário conhecido, origem (operador ou observação controlada), instante e limitações por rota/destino. Sem inventário, informar desconhecido; Pod/nó não comprovam IP público. Vários workers podem compartilhar NAT. Não há descoberta automática, rotação de IP ou proxy de evasão nesta implementação.
