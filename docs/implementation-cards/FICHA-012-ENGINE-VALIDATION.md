# FICHA-012 — Calibração e aceite do primeiro marco

Prioridade: P0. Complexidade: M. Marco: M1. Status: proposta de integração futura; não implementada.

## Objetivo

Demonstrar correção do motor integrado e estabelecer um envelope local medido de capacidade, sem inferir metas universais de RPS.

## Contexto arquitetural

Gate de M1 antes de adicionar banco/CP/Kubernetes. Usa critérios de [08](../08-DEVELOPMENT-ROADMAP.md); resultado da ficha é evidência reproduzível e correções necessárias dentro do motor.

## Conceitos necessários

Benchmark é condicionado por hardware/perfil. CPU do cliente, rede e alvo podem limitar o mesmo experimento. Warm-up precisa ser separado e identificado.

## Responsabilidades

Executar matriz funcional/de falhas, medir CPU/RSS/alocações/sockets/lateness, verificar contagens/percentis e documentar limitações. Corrigir falhas de M1 encontradas e repetir apenas casos afetados e gates essenciais.

## Fora do escopo

Prometer 50k RPS, escalar Minikube, comparar marketing de ferramentas, introduzir engine pronta ou publicar serviço.

## Dependências

FICHA-011; suas dependências transitivas incluem todas as fichas anteriores do marco.

## Contratos

Relatório de validação com ambiente, versões, parâmetros, hash de configuração sanitizada, evidências, repetições, variabilidade e resultado por critério. Distinguir calculado/medido/hipótese. Nenhuma credencial no artefato.

## Integração futura — aceite de Rate Limiting v2

Gate integrado continua pendente; o recorte atual de contratos/parser não encerra M1 nem a FICHA-037. Seguir o [contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12). Evidências atuais estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md); 119 testes da base são históricos. Esta atualização não executa testes nem mede capacidade.

Matriz futura de M1: 429 contínuo/intermitente e após falha de corpo; 503 com Retry-After sem IsRateLimited; NotObserved sem captura versus Missing após captura efetiva sem campo em 429, NotApplicable em outros status e associações inválidas; parser inválido/duplicado/datas/clamp; 200/202 completo e 2xx incompleto; credenciais sintéticas em HTTP loopback nos três modos, sem validação de origem da credencial nem distinção entre segredos reais e sintéticos pelo validador, e Authorized ainda limitado; canários sem vazamento; agenda open-loop idêntica sem retries/sondagens; status contado uma vez; denominador zero/null, fronteiras/drain, categorias fixas/min-max e cobertura insuficiente explícita. Conciliar recebimentos da fixture com inícios e terminais, sem inferir recebimento pelo servidor a partir do cliente.

Registrar resultados e overhead somente após execução real, com ambiente, limites e populações. Integração parcial não autoriza marcar a 037 pronta. Ensaios NAT/egress/IP compartilhado e falhas/replay/intervalo global distribuído são gates somente de M4/M5; cofre/referências e UI/SSE pertencem a M3 e M6. Não simular esses marcos como evidência de produção.

## Decisões técnicas

Modo Release e fixture local real, relógio falso para exatidão e métricas do runtime para recursos. Usar ferramentas adicionais de profiling só para uma hipótese específica; registrar seu overhead.

## Passos de implementação

1. Executar vetores determinísticos de agenda/histograma/invariantes.
2. Aquecer separadamente e iniciar cenários 10/100 RPS por 30 s.
3. Repetir cenário estável pelo menos três vezes, registrar dispersão.
4. Testar alvo lento, falhas, limite de corpo e gerador limitado.
5. Ampliar duração para 2 e 10 min observando plateau; ampliar RPS só com folga.
6. Testar Ctrl+C, SIGTERM, sink bloqueado e falha de gravação em subprocesso.
7. Registrar envelope e corrigir/retestar falhas antes de fechar M1.

## Testes obrigatórios

Todos os gates das fichas anteriores, incluindo erro de percentil, contagem da fixture, no máximo C em voo, nenhuma exceção não observada, limite de buffers e redaction. Cenário sem saturação proposto: inícios dentro de ±2% do plano, zero drops; simulação temporal exige contagem exata. Sob saturação, drops/flags obrigatórios e memória limitada, sem exigência de atingir RPS.

## Observabilidade

Guardar sumário de CPU, RSS, GC/alocações, pool/conexões, atraso de dispatch e fila de saída, com métricas indisponíveis marcadas. Não produzir dumps com secrets sem necessidade.

## Critérios de aceite

Correção matemática e ciclo de recursos aprovados; falhas não geram sucesso silencioso. Pelo menos um cenário local não saturado cumpre o gate e um cenário limitado revela a limitação. Há evidência de memória limitada e relatório reproduzível, sem afirmar que resultado local representa produção.

## Riscos e trade-offs

Mesmo host confunde cliente/alvo; se não houver host separado, declarar diagnóstico inconclusivo para rede/capacidade externa. Falha de gate exige investigação, não reduzir exigência retrospectivamente sem revisão.

## Definição de pronto

- [ ] Evidências e parâmetros reproduzíveis registrados.
- [ ] Correções necessárias de M1 concluídas e verificadas.
- [ ] Envelope medido e limitações revisados antes de detalhar M2.
