# FICHA-006 — Admissão com concorrência limitada

Prioridade: P0. Complexidade: M. Marco: M1. Status: implementada em 2026-10-13; integrada ao host 009 e validada no gate 012 de 2026-10-21.

## Objetivo

Executar chegadas que cabem no limite C e contabilizar imediatamente as que não cabem, sem fila crescente de requisições.

## Contexto arquitetural

Liga agenda 005 ao transporte medido 004 e produz observações para métricas. Não decide o estado persistido de uma execução.

## Conceitos necessários

Concorrência limita trabalho em voo; RPS limita chegadas. Esperar pelo semáforo alteraria o modelo de carga. Little fornece estimativa média, não reserva segura de recursos.

## Responsabilidades

Tentativa de admissão sem espera, no máximo C operações, liberação em todos os caminhos, observação de tarefas e contadores de in-flight/peak/drops.

## Fora do escopo

Fila ilimitada, adaptação de RPS, criar uma thread por request, agregação persistente e autoscaling.

## Dependências

FICHAS 004 e 005.

## Contratos

Para cada due: `started` ou `not_started.concurrency_limit`/policy. Para cada started: exatamente uma observação terminal. In-flight nunca maior que C. Lista/estrutura de acompanhamento remove concluídas e não retém o histórico.

## Decisões técnicas

Semáforo/contador com aquisição não bloqueante e finally; I/O async sem Task.Run por chamada. Usar callback de observação limitado em memória, não channel ilimitado.

## Passos de implementação

1. Reservar slot sem bloquear a agenda.
2. Iniciar transporte e acompanhar somente chamadas ativas.
3. Classificar desfecho e liberar exatamente uma vez.
4. Expor cancelamento/drain ao host futuro.
5. Medir in-flight e recusas com invariantes.

## Testes obrigatórios

Transporte controlado segura C operações e recusa a próxima; liberar uma permite nova chegada. Exceções síncronas/assíncronas e cancelamento não vazam slots. Alvo lento causa drops, sem desacelerar silenciosamente a agenda. Teste de alto total não acumula tasks terminadas.

## Observabilidade

In-flight atual/pico, started e drops; flag generator_limited quando há recusa por capacidade. Falha inesperada é interna, distinta de erro do alvo.

## Critérios de aceite

Invariantes do documento 03 passam com corridas controladas. In-flight ≤C em todos os instantes observáveis; nenhuma fila de requests pendentes existe. Todas as tarefas e exceções são observadas.

## Riscos e trade-offs

Escolher C baixo limita RPS; alto aumenta RAM/sockets. Fila interna do handler ainda pode existir e deve ser medida, não suposta ausente.

## Definição de pronto

- [x] Suíte de liberação/concorrência/cancelamento aprovada.
- [x] Quantidade de objetos acompanhados é limitada por C.
- [x] Saturação aparece como dado do gerador, sem retry oculto.

## Evidência da implementação

`Scheduling/BoundedAdmission.cs`: reserva não bloqueante sob lock curto, callback terminal sem fila, started/completed/in-flight/pico e recusas por concorrência/policy. Sem Task.Run por operação ou retenção de tarefas concluídas. StopAdmissions fecha admissão; DrainAsync espera operações ativas sem cancelar seu I/O. O token passado a TryStart permite cancelamento das operações pelo host futuro. Falha do consumidor fecha admissão e é reportada no drain.

`BoundedAdmissionTests.cs` cobre saturação/liberação, chamadas simultâneas, falhas síncronas/assíncronas, cancelamento, falha do consumidor e 100.000 operações. Integração com transporte HTTP real lento e agenda simulada concilia planned=100, started=2, concurrency_limit=98 e duas respostas 429 completas, sem retry. Esse teste verifica contabilização e independência da agenda, não capacidade do host. Detalhes em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md).
