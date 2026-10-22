# FICHA-015 — Consultas e retenção local

Prioridade: P0. Marco: M2. Status: em implementação. Autorizado em 2026-10-22.

Objetivo: consultar histórico, resumo e janelas importadas, preservando isolamento lógico e completude.

Escopo: comandos CLI de migração/importação/listagem/detalhe/janelas/retenção/medição; paginação por cursor estável e limites; conexão por variável de ambiente, sem credenciais em argumentos ou erros. Resumo durável com buckets e metadados de cobertura. Limpeza limitada de execuções seladas: detalhe após 7 dias, resumo após 30 dias; registros mínimos de identidade/hash mantêm replay explícito enquanto retidos.

Dependências: 013 e 014. Não inclui API HTTP, UI ou autorização multiusuário.

Aceite: consultas cruzadas não retornam dados; paginação sem repetição; retenção preserva resumo e replay; nenhuma execução não selada é removida; tamanho físico/WAL medidos e limitações documentadas. Retenção por idade após término, com lotes pequenos e exclusão de filhos antes do pai.

Validação: pendente.
