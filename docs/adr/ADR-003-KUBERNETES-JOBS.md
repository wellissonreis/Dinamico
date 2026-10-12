# ADR-003 — Um Kubernetes Job por slot

Status: Proposto. Data: 2026-10-08.

## Contexto

Workers existem por execução e precisam de recursos limitados e evidência de término.

## Problema

Gerenciar processos temporários sem scheduler próprio de máquinas nem reexecução acidental de carga.

## Alternativas consideradas

Deployment/pool permanente reduz cold start, mas exige protocolo de alocação e limpeza entre tenants. Um Indexed Job reduz objetos, mas complica políticas/credenciais individuais. Operator/CRD é complexidade sem necessidade atual.

## Decisão

Job por slot, sem retry planejado (`backoffLimit=0`, restart Never), deadline/TTL e registro exclusivo de tentativa no banco. Nomes determinísticos, verificação de UID/hash e ausência de reposição durante RUNNING.

## Justificativa

Isolamento e observação por slot são claros. Jobs não asseguram exactly-once, logo autorização na aplicação é obrigatória mesmo com paralelismo 1.

## Consequências positivas

Recursos e evidência por worker; menos lógica de reutilização; integração direta com Minikube/Helm.

## Consequências negativas

Startup variável, mais objetos Kubernetes e perda de execução quando worker falha. TTL pode apagar evidência se mal configurado.

## Condições para revisar a decisão

Tempo de startup ou quantidade de objetos medidos inviabiliza experiência; avaliar Indexed Job ou pool mantendo exclusividade e segurança.
