# FICHA-015 — Consultas e retenção local

Prioridade: P0. Marco: M2. Status: concluída em 2026-10-22. Autorizado nesta data.

Objetivo: consultar histórico, resumo e janelas importadas, preservando isolamento lógico e completude.

Escopo: comandos CLI de migração/importação/listagem/detalhe/janelas/retenção/medição; paginação por cursor estável e limites; conexão por variável de ambiente, sem credenciais em argumentos ou erros. Resumo durável com buckets e metadados de cobertura. Limpeza limitada de execuções seladas: detalhe após 7 dias, resumo após 30 dias e identidade/hash após 90 dias. Replay permanece válido enquanto a identidade existe; após expiração, nova importação cria entrada.

Dependências: 013 e 014. Não inclui API HTTP, UI ou autorização multiusuário.

Aceite: consultas cruzadas não retornam dados; paginação sem repetição; retenção preserva resumo e replay; nenhuma execução não selada é removida; tamanho físico/WAL medidos e limitações documentadas. Retenção por idade após término, com lotes pequenos e exclusão de filhos antes do pai.

Validação: isolamento entre projetos, paginação sem sobreposição, limites/faixas, execução não selada invisível e protegida, retenção 7/30/90 e replay após remoção de detalhe testados. Fluxo real CLI: 12 verificações aprovadas em `TestResults/M2/Cli/checks.json`; reinício do PostgreSQL Compose preserva execução e replay em `restart.json`. Execução com volume persistente e porta loopback validada. Ver [guia operacional](../M2-LOCAL-PERSISTENCE.md). Backup/restore, autenticação e ingestão distribuída permanecem fora de M2.
