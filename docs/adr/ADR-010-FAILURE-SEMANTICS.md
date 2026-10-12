# ADR-010 — Parar e preservar resultados parciais

Status: Proposto. Data: 2026-10-08.

## Contexto

Reiniciar worker ou compensar carga perdida altera o experimento; banco, rede e processos podem falhar separadamente.

## Problema

Recuperar coordenação sem duplicar tráfego nem afirmar completude inexistente.

## Alternativas consideradas

Recomeçar teste automaticamente pode duplicar carga no alvo. Redistribuir parcelas modifica perfil e concorre com processo antigo. Continuar sem controle reduz previsibilidade da parada e pode perder métricas.

## Decisão

Perda de worker, lease ou integridade interrompe execução; sem reposição durante RUNNING. Estado persistido, efeitos idempotentes, FINALIZING, manifesto e cobertura. Estado terminal não apaga cleanup pendente; parada não comprovada mantém reserva e alerta.

## Justificativa

Prioriza interpretação dos resultados e limitação do tráfego. CP recupera intenção e evidência, não recria requests perdidos.

## Consequências positivas

Falhas ficam explícitas; restart do CP não significa rerun; efeito de partições é limitado por lease e deadlines.

## Consequências negativas

Indisponibilidade curta demais para o usuário pode abortar um teste longo. Não há garantia absoluta de desligar processo em nó congelado/particionado.

## Condições para revisar a decisão

Usuários precisarem de execução tolerante a perdas e aceitarem semântica de replanejamento. Exigirá epochs, fencing externo e relatório do perfil efetivo, não apenas retry do Job.
