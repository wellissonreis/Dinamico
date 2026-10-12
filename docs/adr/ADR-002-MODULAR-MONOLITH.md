# ADR-002 — Control Plane em monólito modular

Status: Proposto. Data: 2026-10-08.

## Contexto

Cadastro, quotas, execução e resultados pertencem ao mesmo produto inicial e compartilham transações.

## Problema

Separar responsabilidades sem criar comunicação distribuída entre cada caso de uso.

## Alternativas consideradas

Microsserviços gerariam autenticação interna, deploys e consistência distribuída sem escala demonstrada. Um módulo único sem fronteiras mistura catálogo com hot path do motor. CQRS com bancos distintos não se justifica.

## Decisão

Um processo ASP.NET Core para API, consultas e tarefas de fundo; módulos com propriedade definida em 02. Worker permanece processo independente por ter perfil de recursos e ciclo de vida diferentes.

## Justificativa

Reserva de quota e início podem ser atômicos no PostgreSQL. Fronteiras explícitas mantêm testabilidade e possibilidade de extração futura.

## Consequências positivas

Deploy e investigação simples; alterações de contratos internos são coordenadas; menos falhas de rede.

## Consequências negativas

Ingestão e API disputam recursos; escala de um módulo replica o processo inteiro. Exige limites de concorrência/pools.

## Condições para revisar a decisão

Contenção medida entre ingestão/reconciliação/API ou equipes com necessidade operacional independente. Extrair somente a fronteira comprovadamente problemática.
