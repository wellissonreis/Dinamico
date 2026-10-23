# FICHA-005 — Agenda open-loop determinística

Prioridade: P0. Complexidade: M. Marco: M1. Status: implementada em 2026-10-13; espera/diagnóstico ampliados e integração M1 validada no gate 012 de 2026-10-21.

## Objetivo

Produzir intenções de chegada na taxa configurada independentemente de quanto demoram as respostas, sem acumular trabalho após pausas.

## Contexto arquitetural

Componente puro de planejamento/tempo do motor. M1 usa W=1; a matemática deve permitir validar partição futura sem implementar coordenação distribuída.

## Conceitos necessários

Open-loop, índice de chegada, tempo monotônico, quantização do timer e diferença entre chegada planejada e envio físico. Fórmula canônica em 03 usa meio intervalo.

## Responsabilidades

Calcular N e timestamps, avançar por índices, reportar due/late e finalizar no limite da duração. Permitir relógio falso.

## Fora do escopo

HTTP, semáforo de concorrência, perfil em degraus, T0 distribuído, threads dedicadas e busy-spin.

## Dependências

FICHA-001.

## Contratos

Entrada: R, D, instante inicial monotônico, quantum e tolerância de atraso. Saída incremental: índice/due para tentativa de admissão e contagem dos descartados por atraso; sem lista de toda a execução. Teto de admissões por despertar conforme 03.

## Integração futura — Rate Limiting v2

Scheduler implementado em `Scheduling/ArrivalSchedule.cs` e `ArrivalScheduler.cs`. Conforme o [contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12), modos Normal/Authorized/Diagnostic, 429 e RetryAfterObservation não alteram RPS, concorrência, índice/due ou fim da agenda. LoadRetryPolicy.None proíbe retry de carga; Diagnostic não cria sondagens. Evidências estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md).

Com relógio falso, comparar a sequência exata de índices/due nos três modos com respostas 429 contínuas/intermitentes, 503 com Retry-After e respostas lentas. Delay válido, clamp ou data de Retry-After não introduzem espera, catch-up, requisição adicional ou recalculam planned_total. Na integração 002–006, conciliar admissões, inícios, drops e recebimentos da fixture sem confundir timeout com ausência no servidor. A sequência com respostas fictícias e a conciliação HTTP lenta foram verificadas na retomada. Execução operacional completa dos três modos depende de 009/012.

## Decisões técnicas

Aritmética inteira/racional e agenda absoluta evitam drift por delays acumulados. Loop assíncrono com quantum inicial 1 ms e limite de atraso 5 ms; números são hipóteses a medir.

## Passos de implementação

1. Implementar cálculo puro do próximo índice e fim do plano.
2. Integrar relógio/espera substituíveis.
3. Saltar atrasos em O(1) por intervalo perdido.
4. Aplicar teto de admissões por despertar, sem fila de catch-up.
5. Expor contadores de atraso ao pipeline.

## Testes obrigatórios

R=1 e R alto, duração mínima, relógio que salta, término exato, overflow recusado, ausência de drift em tempo simulado longo. Com respostas fictícias lentas, agenda não muda. Como teste matemático adicional, união das parcelas n mod W não duplica nem perde índices, inclusive R<W.

## Observabilidade

Due, não iniciado por atraso, atraso máximo e número de despertares limitados; sem um log por intenção.

## Critérios de aceite

Contagem planejada exata sob relógio falso; índices estritamente crescentes; atraso grande não causa rajada ou iteração proporcional ao total perdido. Nenhuma estrutura cresce com R×D.

## Riscos e trade-offs

Timers não são real-time; microbateladas são esperadas. Um token bucket permissivo mudaria o perfil e não substitui essa especificação sem revisão.

## Definição de pronto

- [x] Testes determinísticos cobrem bordas/pausas.
- [x] Tempo/memória por salto não dependem de requests perdidas.
- [x] Quantum e tolerância constam no contrato e no relatório JSON, incluindo diagnósticos por execução.

## Evidência da retomada

`ArrivalSchedulerTests.cs` valida fórmula racional, partições, saltos em O(1), teto por despertar, fim, overflow e invariância dos modos com respostas fictícias. Corrigido CS9124 no relógio falso que impedia compilar o trabalho pendente. `BoundedAdmissionTests.cs` liga agenda simulada, admissão e HTTP real; calibração do relógio real permanece no gate 012.

## Continuação — espera e diagnóstico (2026-10-21)

`SchedulerWait` usa timer de alta resolução no Windows, registrado no ThreadPool e cancelável, sem busy-spin, thread por operação ou alteração da resolução global. O Linux mantém `Task.Delay` com arredondamento explícito para milissegundos. A escolha Windows usa [CreateWaitableTimerExW](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw) e `SetWaitableTimerEx` com prazo relativo. O critério de atraso de 5 ms, quantum de 1 ms, agenda absoluta e teto por microbatelada permanecem os mesmos.

`result.scheduler` publica despertares, despertares limitados, atraso máximo observado no poll e descartes por tolerância, teto e fim. Esses diagnósticos cobrem os polls; o controlador pode registrar descartes adicionais na parada entre polls. São separados do atraso de registro de métricas e não medem chegada ao servidor. Quantum e tolerância também constam em `plan.limits`.

Testes de tempo real verificam que a agenda não oferece chegadas antes do due e que cancelamentos repetidos permitem nova espera; teste determinístico concilia os três grupos de descarte. Uma espera individual pode retornar cedo, como já previsto pelo contrato e pelos testes de relógio falso; a agenda relê o relógio e espera novamente. Build Release sem avisos/erros; 67 direcionados Windows iniciais aprovados (`TestResults/M1/Timing/`) e 13 direcionados Linux finais de agenda/HTTP/lifetime (`TestResults/M1/FinalDirected/`). Comparação inicial Windows: baseline 87/300 e 962/3.000 inícios; com a nova espera, 300/300 (zero drops, atraso máximo 3,6335 ms) e 2.999/3.000 (um drop, máximo 5,8735 ms). É uma comparação local, não garantia de tempo real ou aceite completo de capacidade. Repetições e recursos pertencem à [012](FICHA-012-ENGINE-VALIDATION.md).
