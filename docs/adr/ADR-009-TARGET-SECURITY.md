# ADR-009 — Autorização de destino e isolamento obrigatório

Status: Proposto. Data: 2026-10-08.

## Contexto

Uma plataforma de carga pode causar abuso e SSRF mesmo quando o usuário está autenticado.

## Problema

Impedir destinos não autorizados, redes internas e vazamento de credenciais sem infraestrutura de proxy adicional.

## Alternativas consideradas

Somente checkbox/termos não comprova controle. Somente validação DNS no cadastro permite rebinding. Egress proxy central adiciona gargalo e altera a rede medida. NetworkPolicy sozinha não valida hostname.

## Decisão

MVP por convite, grant por hostname exato com prova DNS e aprovação do operador, quotas por destino, IP público pinado no transporte e egress restrito via CNI. Sem redirects/proxy ambiente; secrets protegidos e isolados por execução. Laboratório loopback/fixture separado de produção.

## Justificativa

Defesas complementares cobrem autorização e destino efetivo. A aprovação reconhece que domínio/CDN compartilhado não comprova direito de testar toda infraestrutura.

## Consequências positivas

Reduz SSRF e abuso com componentes da baseline; falha fechada verificável.

## Consequências negativas

Onboarding manual, DNS pinado pode falhar em mudança legítima e políticas por execução geram objetos. Não suporta rede privada do cliente.

## Condições para revisar a decisão

Necessidade de agentes privados, volume de grants inviável ou limitações do CNI demonstradas. Exigir novo modelo de ameaça e validação, não um bypass de produção.
