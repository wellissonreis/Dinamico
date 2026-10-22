# FICHA-013 — PostgreSQL e revisões locais

Prioridade: P0. Marco: M2. Status: em implementação. Autorizado em 2026-10-22.

Objetivo: persistir resultados do motor local sem introduzir Control Plane ou acesso remoto.

Escopo: projeto lógico UUID informado pelo operador; snapshot sanitizado, imutável e versionado; migrations SQL transacionais com checksum; Npgsql e SQL explícito; PostgreSQL local isolado. O projeto lógico delimita consultas e não equivale a autenticação ou autorização M3.

Dependência: 012 concluída. Decisão: manter a engine sem dependência de PostgreSQL; o adaptador fica em `src/Dinamico.Persistence`. Reutilizar codecs binários validados de janelas/histogramas e rejeitar valores fora da faixa bigint antes do commit.

Aceite: migrations repetíveis e concorrentes; compatibilidade de schema verificada; restore locked; isolamento lógico testado; medir tamanho físico e WAL em ensaio reproduzível. Sem segredos, URL ou headers na persistência.

Validação: pendente. Continuidade e resultados nas fichas 014/015.
