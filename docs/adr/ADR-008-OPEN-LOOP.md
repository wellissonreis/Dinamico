# ADR-008 — Chegadas open-loop com partição fixa

Status: Proposto. Data: 2026-10-08.

## Contexto

Usuário configura RPS e precisa saber se o alvo suporta aquela demanda.

## Problema

Não deixar a latência do alvo reduzir silenciosamente a taxa nem criar rajadas de compensação após pausas.

## Alternativas consideradas

Closed-loop responde outra pergunta, baseada em usuários concorrentes. Rate limiter central por request acopla worker ao CP. Token bucket grande acumula burst. Redis distribuído não garante temporização física e viola baseline.

## Decisão

Agenda monotônica de chegadas, partição por índice modulo W, T0 coordenado e admissão sem espera. Contar atrasos/drops e não redistribuir após perda de worker. Sem alteração de RPS/W durante a execução.

## Justificativa

Soma das parcelas é verificável e não requer comunicação por request. Transparência de carga não iniciada evita interpretar gerador saturado como alvo rápido.

## Consequências positivas

Sem gargalo central de geração; testes determinísticos; plano reproduzível.

## Consequências negativas

Jitter de sistema operacional, relógios e capacidade do worker impedem garantia de RPS exato no fio. Recursos subdimensionados produzem drops explícitos.

## Condições para revisar a decisão

Produto exigir usuários virtuais/jornadas ou distribuição probabilística de chegadas. Adicionar modo com semântica própria; não mudar o significado de RPS existente.
