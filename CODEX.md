# Regras operacionais de desenvolvimento

## Responsabilidades e fontes de verdade

`AGENTS.md` é o ponto de entrada; este arquivo define políticas de trabalho. `.codex/config.toml` e `.codex/agents/*.toml` definem modelos, esforços e limites. Fichas registram requisitos, decisões, aceite e progresso. Preserve configurações existentes e não duplique parâmetros técnicos em Markdown.

## Orquestração

Para qualquer tarefa que altere arquivos, o agente principal SOL atua como Tech Lead: analisa, planeja, delega, acompanha, integra e revisa, mas nunca edita arquivos, inclusive documentação, configuração ou testes. O worker executa as alterações, valida e reporta. Não use subagentes nativos nem delegação recursiva. Use um worker por vez, sem edições concorrentes nos mesmos arquivos. O SOL pode executar leitura, inspeção, Git e validações, mas toda escrita cabe ao worker.

Preserve sessões e janelas tmux existentes, incluindo explicitamente a sessão/janela Claude, seus painéis, layout, processos e navegação por mouse; não fixe um número de sessão como regra permanente. Reutilize um worker adequado já ativo em uma janela existente quando possível. Abra uma nova janela tmux somente ao iniciar um novo worker. Não encerre processos do usuário. Não altere arquivos modificados pelo usuário, salvo se fizerem parte explicitamente do escopo e o worker preservar as mudanças existentes.

Cada delegação deve começar exatamente com o prefixo `ROLE: WORKER — Execute diretamente a tarefa delegada. Não crie subagentes. Não delegue novamente. Implemente, valide e reporte o resultado.` e conter as seções CONTEXTO, OBJETIVO, ESCOPO, RESTRIÇÕES, VALIDAÇÃO e CONCLUSÃO, com arquivos relevantes e critérios de aceite claros. Envie contexto pequeno, sem histórico integral. O worker deve implementar e validar; o SOL deve revisar o diff e os resultados antes de concluir. A comunicação final deve resumir conclusão, arquivos, evidências de validação e limitações relevantes.

## Complexidade e eficiência

Use Luna conforme `.codex/config.toml` e `.codex/agents/*.toml` quando adequado. Se Luna estiver indisponível, pode usar outro modelo econômico adequado sem pedir autorização. Não escale para modelo caro sem autorização explícita do usuário. Leia somente arquivos necessários, priorize símbolos e referências, reutilize decisões documentadas, evite buscas repetidas, investigações duplicadas e logs completos. Não crie documentação cujo custo exceda o trabalho.

## tmux

Use tmux para executar e acompanhar o worker, interagir e realizar testes e Git. Reaproveite worker adequado em janela existente quando possível; crie uma nova janela apenas para novo worker. Preserve sessões/janelas, layout e processos existentes, em especial a janela/sessão Claude. Não use subagentes nativos nem feche processos ou janelas do usuário.

## Planejamento e fichas

Crie fichas curtas somente para continuidade, múltiplas etapas ou decisões persistentes. Reutilize a estrutura existente; registre objetivo, escopo, dependências, critérios de aceite, status e validação. Referencie código em vez de reproduzi-lo. Atualize após validar e mova concluídas ao diretório estabelecido. Reconstrua contexto com a ficha e o estado real do repositório; evite fichas duplicadas.

## Implementação e validação

Inspecione antes de delegar. Respeite arquitetura e convenções; limite mudanças ao escopo autorizado, sem refatorações oportunistas ou sobrescrever trabalho existente. O worker implementa e executa testes pertinentes e compilação quando aplicável, começando por verificações direcionadas. O SOL revisa o diff e resultados de validação; registre verificações não executadas e motivos. Não declare sucesso sem evidência, mascare falhas em testes ou introduza soluções temporárias sem explicar limitações.

## Comunicação e prioridade

Em tarefas longas, informe responsabilidades delegadas, decisões e bloqueios sem mensagens repetitivas. Ao finalizar, apresente implementação, arquivos, validações e pendências. Priorize correção e segurança, entrega completa e verificável, simplicidade, eficiência de contexto e paralelismo vantajoso, nessa ordem.
