# FICHA-009 — Ciclo de vida e parada local

Prioridade: P0. Complexidade: M. Marco: M1. Status: concluída em 2026-10-17; CLI/observabilidade integradas e gate 012 validado em 2026-10-21.

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

- [x] Corridas e caminhos de falha testados.
- [x] Sem tráfego novo após parar a agenda.
- [x] Qualidade e estado final são campos distintos.

## Decisões da continuação — 2026-10-17 (antes da implementação)

Um controlador local de uso único integra os componentes 003–008, sem mudar a CLI informativa (011) ou antecipar logging/amostras (010). Transições internas limitadas a prepared/generating/draining/stopping/finished; resultado separado em razão, falhas, qualidade, cobertura de métricas, operações drenadas e flush confirmado. Sem reter plano sensível, exceção bruta, tasks concluídas ou lista de janelas no resultado.

Primeira parada anormal é latched; fim natural encerra somente a geração, permitindo cancelamento durante drain. Falhas posteriores são registradas separadamente, mesmo quando cancelamento foi a primeira razão. Serializar admissão/parada para impedir novos inícios após stop. Reconciliar due pelo instante latched de fim da geração (limitado ao plano), incluindo chegadas vencidas desde o último poll; restantes são policy em parada anormal ou scheduler_late em fim natural. O restante futuro do plano é not_scheduled_after_stop, nunca drop de capacidade.

O transporte recebe token de aborto e callback de headers; início é registrado antes do envio, headers antes do corpo e terminal uma vez. InternalFailed interrompe a execução. O timer de rotação continua no drain. Consumidor local assíncrono mantém a janela no sink até WriteAsync confirmar; no máximo seis janelas ainda não confirmadas, sem fila adicional. Flush tem teto de 10 s após fechamento; cancelamento do usuário não cancela flush automaticamente. O resultado conserva totais do prefixo comprovado, incluindo janelas ainda retidas, sem transformar lacuna em zero.

I/O externo deve honrar cancelamento. Deadline do transporte limita drain natural; controlador tem teto adicional de RequestTimeout + 1 s de tolerância de host e aborto/grace de 1 s. Se adaptador injetado não cooperar, marcar operações/saída incompletas, observar eventual falha e adiar dispose do transporte até terminar a operação; nunca declarar encerramento de recursos que não ocorreu. O transporte padrão usa cancelamento em todo I/O. Esses casos estruturais são distintos da conclusão normal e testados sem ocultar tarefas.

## Entrega — API local

`Lifecycle/LocalRunController.cs` conduz uma execução por instância, integrando agenda, admissão, transporte e coletor. `Lifecycle/LocalRunResult.cs` define estados/transições (no máximo cinco), reason/failure flags, resultado sanitizado e `ILocalMetricOutput`. O plano não é retido no resultado nem serializado; não há exceção bruta ou histórico por request/janela. A mensagem informativa da CLI foi corrigida, sem implementar parsing/execução da 011.

`RunAsync(plan, output, cancellationToken)` requer saída explícita. WriteAsync aceita uma janela imutável e confirma sua gravação; FlushAsync confirma os dados e recebe manifesto completo, parcial ou null quando cobertura final não pôde ser comprovada. Cancelamento do usuário aborta I/O, mas não cancela o flush automaticamente. A saída pertence ao chamador. Implementações devem respeitar token e não criar fila/retenção ilimitada. O ciclo mantém a janela no sink até confirmar WriteAsync; o controle/finalização não depende de obter um novo slot da fila.

A recusa da admissão agora informa policy ou concurrency_limit diretamente, sem inferir por snapshots concorrentes. Início é registrado antes do envio; callback de headers não é repetido no terminal. Resultado mantém `PlannedTotal`, `ScheduledDue` (horário alcançado antes da parada) e `NotScheduledAfterStop`, duração real da geração/drain/flush e contadores do prefixo comprovado. Totais do prefixo incluem janelas ainda retidas em memória, sem alegar que foram confirmadas na saída. `MetricsCoverageComplete`, `OperationsDrained`, `FlushConfirmed` e `OutputStopped` são independentes; `DataComplete` exige todos. Completed pode coexistir com GeneratorLimited/TargetErrors; falha interna pode ter dados integralmente registrados. Cancelamento com flush falho mantém a primeira razão UserCancelled e marca Completion=Failed com a falha adicional.

Há fallback de drain (RequestTimeout + 1 s e grace de aborto de 1 s) e flush (10 s e grace de cancelamento de 1 s), usando TimeProvider para teste de prazo. O adaptador padrão transmite cancelamento a todo I/O. Se um adaptador interno de teste ignorar cancelamento, o ciclo retorna estado incompleto, observa eventual falha e só dispõe o transporte depois do término real; OutputStopped=false explicita flush ainda pendente. Não se promete forçar término de código externo não cooperativo. Testes virtuais provam os prazos; tolerância real do host/calibração e SIGTERM em subprocesso pertencem à 012.

## Bugs encontrados e corrigidos

- Corrida da primeira causa: rotação podia bloquear o coletor por sink cheio e liberar o lock do ciclo antes de latchar TelemetryBackpressure. Outro evento recebia InvalidOperationException por coletor já bloqueado e latcheava InternalFailure primeiro. Detecção, sinalização e latch agora ocorrem na mesma seção crítica. Teste do writer bloqueado reproduziu a falha na rodada ampliada; causa preservada após a correção.
- Sinalização do sink: TryRead numa fila vazia podia substituir o sinal de um consumidor PeekAsync já aguardando, perdendo seu despertar. Sinal só é renovado depois de concluído, quando a fila volta a ficar vazia. Regressão específica cobre esse interleaving, reserva até ACK e conclusão da fila.

A revisão final também garante que o task do flush seja devolvido antes da chamada à saída externa, instalando o deadline mesmo quando a saída faz trabalho síncrono antes de devolver seu awaitable. Uma suspensão assíncrona por flush; nenhum Task.Run por requisição. Revalidado pelos direcionados e suíte completa abaixo.

## Validação real — 2026-10-17

Testes adicionados somente para comportamentos específicos/riscos e bugs: 19 casos de lifecycle/sink, incluindo cancelamento antes/durante geração/drain, EOF concorrente com cancelamento (dez corridas por caso), due entre polls, drops/limite de concorrência, gerador atrasado, separação de status e conclusão no drain, falha inesperada, primeira razão com falha posterior, writer parado e seis janelas sem ACK, falha de saída, deadline de flush, adaptadores não cooperativos e ordem de dispose. HTTP real em fixture loopback para 202/429/503 usa agenda/tempo simulados para isolar o ciclo; não é calibração de RPS do host. Nenhum teste de cadastro/CRUD trivial foi criado.

```sh
dotnet.exe restore Dinamico.slnx --locked-mode -p:RestoreFallbackFolders= -p:RestoreAdditionalProjectFallbackFolders=
dotnet.exe test tests/Dinamico.LoadEngine.Tests/Dinamico.LoadEngine.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~LocalRunLifecycleTests|FullyQualifiedName~BoundedAdmissionTests|FullyQualifiedName~MetricWindowTests|FullyQualifiedName~TimingDeadlineTests|FullyQualifiedName~LocalHttpTransportTests' --logger trx --results-directory TestResults/Ficha009/ValidatedDirected
dotnet.exe build Dinamico.slnx --configuration Release --no-restore
dotnet.exe test Dinamico.slnx --configuration Release --no-build --no-restore --logger trx --results-directory TestResults/Ficha009/ValidatedSuite
git diff --check
```

Resultados finais: restore locked aprovado, lockfile inalterado; **97/97 direcionados**, **515/515 na suíte completa**, zero falhas/ignorados; build Release zero avisos/erros. TRX: `TestResults/Ficha009/ValidatedDirected/welli_WELL-PC_2026-10-17_18_14_45.trx` e `TestResults/Ficha009/ValidatedSuite/welli_WELL-PC_2026-10-17_18_14_54.trx`. Logs em `/tmp/dinamico-ficha009-{restore,validated-directed,validated-build,validated-suite}.log`.

Repetição focada após corrigir a corrida: dez execuções do filtro `FullyQualifiedName~BlockedWriterRetainsSixUnacknowledgedWindowsAndStopsWithAPartialPrefix|FullyQualifiedName~CancellationRacingEofNeverDuplicatesTerminalOrDisposesAnActivePool`, com `--configuration Release --no-build --no-restore --logger trx --results-directory TestResults/Ficha009/Races/{1..10}`: **20/20 casos aprovados**, incluindo cem corridas internas EOF/cancelamento. Evidência nos dez diretórios TRX e `/tmp/dinamico-ficha009-races.log`.

Histórico desta tarefa: primeiro build de produção aprovado; compilação inicial dos testes corrigida por CS0108 (TimeProvider) e uso inadequado de Assert.Throws com retorno Task (CS0619/xUnit2014). Primeira rodada de lifecycle: 18 aprovados. Rodada ampliada: 96 aprovados/1 falha (corrida de causa acima), TRX `TestResults/Ficha009/ValidatedDirected/welli_WELL-PC_2026-10-17_18_10_48.trx`; correção e resultados finais recompilados acima. Os 496 testes da 008 e demais entregas são evidências anteriores, preservadas.

## Commits e continuidade

Após autorização do usuário, pré-requisitos pendentes foram versionados por responsabilidade, sem incluir `.codex/`, AGENTS.md ou CODEX.md locais: `d665845` histograma, `c1630a2` agenda, `e9f87c4` transporte/deadline, `a9e7c60` admissão, `8b14fe4` janelas e `4af40eb` registros até 008. Continuação: `c8ab487` contrato de resultado/handoff com ACK e `7290db7` controlador e regressões, `c3c1593` proteção do início do flush antes da saída externa. Alterações de trabalho foram preservadas; commits anteriores desta sessão não existiam na evidência histórica 007/008.

Próxima ficha: 010 — eventos/amostras sanitizados e limitados, supressão e saída lenta. CLI/relatório 011 e calibração/gate 012 ainda pendentes; transporte 003 mantém vetores restantes. Não foram implementados serviços, SQL, Control Plane, Kubernetes ou React. M1 e FICHA-037 seguem incompletos. Não há benchmark de CPU/RSS/lock nesta entrega.
