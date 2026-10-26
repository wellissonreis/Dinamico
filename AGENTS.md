# Instruções do projeto

Leia e aplique [CODEX.md](CODEX.md) antes de trabalhar. Configurações técnicas de modelos, esforços e limites estão em `.codex/config.toml` e `.codex/agents/*.toml`.

Para qualquer tarefa que altere arquivos, aplique a orquestração SOL → worker definida em `CODEX.md`: o agente principal SOL analisa, planeja, delega e revisa, mas nunca edita arquivos. O worker implementa, valida e reporta. Não use subagentes nativos nem delegue novamente. Preserve as sessões e janelas tmux existentes, inclusive a janela/sessão Claude, processos e arquivos do usuário. Reutilize um worker adequado já ativo em janela tmux existente quando possível; abra nova janela somente ao iniciar novo worker.
