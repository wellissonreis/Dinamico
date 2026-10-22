# FICHA-013 — PostgreSQL e revisões locais

Prioridade: P0. Marco: M2. Status: concluída em 2026-10-22. Autorizado nesta data.

Objetivo: persistir resultados do motor local sem introduzir Control Plane ou acesso remoto.

Escopo: projeto lógico UUID informado pelo operador; snapshot sanitizado, imutável e versionado; migrations SQL transacionais com checksum; Npgsql e SQL explícito; PostgreSQL local isolado. O projeto lógico delimita consultas e não equivale a autenticação ou autorização M3.

Dependência: 012 concluída. Decisão: manter a engine sem dependência de PostgreSQL; o adaptador fica em `src/Dinamico.Persistence`. Reutilizar codecs binários validados de janelas/histogramas e rejeitar valores fora da faixa bigint antes do commit.

Aceite: migrations repetíveis e concorrentes; compatibilidade de schema verificada; restore locked; isolamento lógico testado; medir tamanho físico e WAL em ensaio reproduzível. Sem segredos, URL ou headers na persistência.

Validação: restore locked e build Release sem avisos/erros; PostgreSQL 16.10 real, migrations concorrentes, checksum/versão desconhecida e limites Int64 testados. Evidências em `TestResults/M2/Final/`. O ensaio `TestResults/M2/Storage/synthetic-final.json` mediu 30 imports por população: crescimento físico de 106.496 bytes (esparso, um bucket) e 65.536 bytes (denso, 765 buckets); WAL de cluster 78.672 e 89.728 bytes. Medianas 5,369/5,1404 ms e P95 6,4618/6,6785 ms. Alocação/compressão afetam a comparação; não são estimativa por request nem capacidade HTTP. Procedimento em [M2 local](../M2-LOCAL-PERSISTENCE.md).
