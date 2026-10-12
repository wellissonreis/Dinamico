# FICHA-009 — Ciclo de vida e parada local

Prioridade: P0. Complexidade: M. Marco: M1. Status: proposta.

## Objetivo

Iniciar uma execução local, cessar geração no prazo ou cancelamento e fechar recursos/métricas com resultado explícito.

## Contexto arquitetural

Host local do motor, sem CP. Seus estágios são internos e não constituem uma segunda máquina de estados persistida do SaaS.

## Conceitos necessários

Parar novas chegadas difere de cancelar I/O ativo. Drain natural preserva respostas; cancelamento explícito busca interromper carga. Conclusão do processo e completude dos dados são distintas.

## Responsabilidades

Conduzir prepared→generating→draining→finished; no cancelamento/falha, ir para stopping antes de finished. Guardar primeiro motivo, controlar prazo, drenar/abortar, fechar janelas e liberar pool depois das tarefas.

## Fora do escopo

Kubernetes, leases remotos, recuperação após crash de processo, estado de banco e retry automático de execução.

## Dependências

FICHA-008.

## Contratos

Run recebe plano e token externo; retorna resultado com razão de término, carga planejada/due/não agendada, duração real, flags e completude. Duração normal para agenda; timeout por request limita drain; flush local tem prazo de 10 s. Sink cheio interrompe geração e retorna parcial.

## Decisões técnicas

Controlador único do ciclo, cancelamento estruturado e finally. Cancelamento natural só fecha agenda; Ctrl+C/SIGTERM cancelam operações. Nenhum processo termina “com sucesso” escondendo exceções de tasks.

## Passos de implementação

1. Integrar componentes com ownership explícito.
2. Definir precedência/latched reason nas corridas de término.
3. Implementar drain normal e aborto explícito.
4. Fechar janelas, obter confirmação do sink e liberar recursos.
5. Preservar resumo parcial quando falha impede finalização integral.

## Testes obrigatórios

Cancelamento antes do início, durante transporte e durante drain; fim concorre com cancelamento; sink bloqueado; exception inesperada; segunda tentativa de cancelar é idempotente. Após término, zero novas chamadas e nenhuma tarefa não observada. SIGTERM será exercitado em subprocesso na 012.

## Observabilidade

Eventos locais de início, geração encerrada, drain/stop e término, com reason_code e flush confirmado. Contadores distinguem not_scheduled_after_stop de drops.

## Critérios de aceite

Cada execução produz um resultado ou falha estrutural identificável, sem sucesso silencioso. Recursos se encerram na ordem correta e limites de drain/flush são respeitados dentro da tolerância do host registrada.

## Riscos e trade-offs

SIGKILL/OOM podem impedir relatório; o marco local não promete persistência nesse caso. Cancelamento pode fechar conexões antes do corpo completo.

## Definição de pronto

- [ ] Corridas e caminhos de falha testados.
- [ ] Sem tráfego novo após parar a agenda.
- [ ] Qualidade e estado final são campos distintos.
