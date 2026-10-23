# Regras operacionais de desenvolvimento

## Responsabilidades e fontes de verdade

`AGENTS.md` é o ponto de entrada; este arquivo define políticas de trabalho. `.codex/config.toml` e `.codex/agents/*.toml` definem modelos, esforços e limites. Fichas registram requisitos, decisões, aceite e progresso. Preserve configurações existentes e não duplique parâmetros técnicos em Markdown.

## Orquestração

O agente principal atua como Tech Lead: analisa, delega, acompanha, integra e valida. Trabalhe com um único agente por padrão. Delegue somente com vantagem concreta; prefira até dois subagentes simultâneos, usando um terceiro somente para tarefas realmente independentes. Não delegue tarefas triviais nem execute tarefas dependentes em paralelo. Evite delegação recursiva; encerre agentes após suas responsabilidades. Não permita edições concorrentes nos mesmos arquivos. Avalie worktrees somente quando isolamento for necessário.

Cada delegação deve conter objetivo, módulos ou arquivos relevantes, restrições, critérios de aceite e resultado esperado. Envie contexto pequeno, sem histórico integral. A resposta deve limitar-se a conclusão, alterações, testes e bloqueios relevantes.

## Complexidade e eficiência

Use os modelos configurados nos TOML conforme a dificuldade real. Eleve esforço somente após investigar causas e identificar riscos ou dificuldades concretas; esforços superiores exigem justificativa. Comunique substituições por indisponibilidade. Leia somente arquivos necessários, priorize símbolos e referências, reutilize decisões documentadas, evite buscas repetidas, investigações duplicadas e logs completos. Não crie documentação ou agentes cujo custo exceda o trabalho.

## tmux

Preserve a sessão, o layout, processos e navegação por mouse existentes. Use tmux para acompanhamento visual, interação, testes e Git; use subagentes nativos para delegação e `/subagents` quando disponível. Não suponha que agentes aparecerão nos painéis. Crie painéis ou sessões independentes somente com benefício concreto de visualização ou execução simultânea. Não feche processos ou painéis do usuário sem autorização.

## Planejamento e fichas

Crie fichas curtas somente para continuidade, múltiplas etapas ou decisões persistentes. Reutilize a estrutura existente; registre objetivo, escopo, dependências, critérios de aceite, status e validação. Referencie código em vez de reproduzi-lo. Atualize após validar e mova concluídas ao diretório estabelecido. Reconstrua contexto com a ficha e o estado real do repositório; evite fichas duplicadas.

## Implementação e validação

Inspecione antes de alterar. Respeite arquitetura e convenções; limite mudanças ao escopo autorizado, sem refatorações oportunistas ou sobrescrever trabalho existente. Não altere produção em tarefas documentais. O principal integra mudanças. Execute testes pertinentes e compilação quando aplicável, começando por verificações direcionadas. Revise falhas, regressões e diferenças do Git; registre verificações não executadas e motivos. Não declare sucesso sem evidência, mascare falhas em testes ou introduza soluções temporárias sem explicar limitações.

## Comunicação e prioridade

Em tarefas longas, informe responsabilidades delegadas, decisões e bloqueios sem mensagens repetitivas. Ao finalizar, apresente implementação, arquivos, validações e pendências. Priorize correção e segurança, entrega completa e verificável, simplicidade, eficiência de contexto e paralelismo vantajoso, nessa ordem.
