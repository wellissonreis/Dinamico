# 08 — Roadmap de desenvolvimento

## Estado atual e escopo autorizado (2026-10-12)

Estado atual (2026-10-12): contratos/validação v2 e parser Retry-After implementados (001/037). A continuação adiciona a fixture HTTP loopback da FICHA-002 e seus testes. FICHA-037 permanece **M1 parcial**: transporte do motor, scheduler, agregação, relatório, Control Plane e UI pendentes. Evidências e limites em [RATE-LIMITING-DELIVERY](RATE-LIMITING-DELIVERY.md).

O [contrato local v2 de 03](03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12) fixa SchemaVersion 2, modos Normal/Authorized/Diagnostic, retry None, `HttpSuccessRule.Completed2xxV1`, índice de header e declaração local para Authorized. Credenciais também são aceitas nos outros modos. Evidências da extensão estão em [RATE-LIMITING-DELIVERY](RATE-LIMITING-DELIVERY.md).

Regra histórica (2026-10-08): aquela entrega terminava na documentação e exigia revisão/autorização antes da implementação. Cada marco abaixo continua com comportamento observável e gate próprio; o recorte atual não entrega os marcos seguintes.

RetryAfterKind tem default NotObserved (captura não realizada); NotApplicable identifica status diferente de 429, e Missing exige captura efetiva em 429 sem campo. Não inferir ausência do header a partir de NotObserved. Conforme [07 — segurança](07-SECURITY.md), HTTP com credenciais sintéticas em loopback é permitido no laboratório; o validador não verifica emissão, propriedade ou validade da credencial nem distingue segredos reais de sintéticos. Usar somente segredos sintéticos nesses testes; HTTPS e os controles remotos permanecem requisitos de M3.

## Fases e dependências

| Marco | Entrega funcional | Validação de saída | Dependência |
|---|---|---|---|
| M0 — revisão | Contratos, riscos e fichas aprovados | Resolver decisões que bloqueiam M1; revisar semântica e tolerâncias | Esta documentação |
| M1 — motor local | CLI executa HTTP/1.1 em fixture loopback, RPS constante, limites, cancelamento e relatório confiável | FICHAS 001–012; sem perdas silenciosas ou recursos ilimitados | M0 |
| M2 — persistência | Importar/consultar resultado local e revisões em PostgreSQL; replay de janelas idempotente | Falha entre insert/merge; medição de tamanho físico, retenção e isolamento lógico | M1 |
| M3 — controle protegido | API local autenticada gerencia projetos, snapshots, quotas e ciclo de execução com worker local | Identity, autorização, secrets, estado persistido, recuperação de processo e teste cruzado de tenants | M2 |
| M4 — um Job seguro | Mesma execução em worker no Minikube, Helm e egress/recursos aplicados | SSRF adversarial, CNI real, Job duplicado, cancelamento e reinício CP | M3 |
| M5 — vários workers | Barreira/T0, divisão fixa, ingestão consolidada e flags de qualidade | Soma das parcelas, atraso de Pod, lease perdido, falha de worker sem reposição | M4 |
| M6 — interface e histórico | React configura, inicia, acompanha SSE, cancela e consulta resultados; perfil em degraus | Fluxo usuário completo, dados atrasados/parciais, revogação de sessão e paginação | M5 |
| M7 — prontidão do MVP | Recuperação endurecida, backup/restore, quotas calibradas e operação por convite | Matriz de falhas, evidência de segurança e limites publicáveis | M6 |

Métricas fazem parte de M1; M2 adiciona durabilidade e M5 consolidação distribuída. Autenticação antecede execução remota; histórico começa pela consulta em M2 e ganha UI em M6. Recuperação mínima começa com persistência/CP, não é adiada por completo até M7. O monólito só será separado em serviços se houver limite comprovado.

## Primeiro marco e ordem das fichas

As fichas detalhadas estão em [implementation-cards](implementation-cards/README.md). Ordem sugerida: contratos e limites (001), fixture controlada (002), transporte streaming (003), duração/deadline (004), agenda open-loop (005), admissão concorrente (006), histograma (007), janelas/contadores (008), ciclo local (009), logs/amostras (010), CLI/relatório (011), calibração integrada (012).

005 e 007 podem ser desenvolvidas independentemente do transporte após 001, mas a sequência listada favorece aprendizado. Dependências são técnicas, não ordem obrigatória de commits. Não há estimativas em dias; complexidade relativa está em cada ficha e pode ser revisada após M1.

## Integração futura da FICHA-037

002/003 integram fixture e transporte: 429 contínuo/intermitente, Retry-After comprovadamente ausente após captura (Missing), inválido ou duplicado, 503 com Retry-After sem sinal de limitação, e falha de corpo após 429. 004 preserva status e tempo monotônico; 005 prova a mesma agenda nos três modos, sem espera ou retry. 008 registra status uma vez nos headers, categorias/clamp/incerteza em acumulador fixo e codec versionado, com soma verificada. 010/011 verificam canários, plano sanitizado e relatório; 012 reúne o aceite de M1. A fixture 002 é entregue na continuação; a integração com o motor e as demais fichas permanecem futuras.

Relatório futuro: percentual de 429 sobre status finais recebidos (null sem respostas), sucesso somente 2xx completo, duração real/drain, populações e cobertura. Somar contadores e buckets antes de derivar percentuais/quantis; inícios não comprovam recebimento no servidor. Cobertura insuficiente deixa o diagnóstico inconclusivo.

M3 adiciona cofre e referências versionadas; declaração local não o antecipa. **Somente M4/M5** exigem inventário de egress, origem/validade da informação e ensaio de NAT compartilhado: IP de Pod/nó não prova IP público nem independência entre workers. M5 verifica replay, cobertura parcial e intervalo global comum, sem somar durações de workers. M6 entrega UI; nenhum desses componentes é criado agora.

## Protocolo de calibração local (planejado)

Registrar CPU/RAM/SO, energia/virtualização, SDK/runtime exatos, modo Release, arquitetura, HTTP, tamanhos, C, timeout, versão do código e posição de cliente/fixture. Aquecer o runtime em uma execução separada registrada; não misturar aquecimento oculto no resultado.

Começar a 10/100 RPS em 30 s com resposta pequena. Critério proposto para cenário comprovadamente não saturado: contagem de inícios dentro de ±2% do planejado em 30 s, sem drops, e invariantes exatas. A tolerância é critério de engenharia a validar, não capacidade prometida. Relógio falso exige contagem exata; o SO real tem jitter. Se o host não cumprir, diagnosticar e registrar limite; não ampliar tolerância para fazer o teste passar.

Ensaiar alvo lento, respostas 500, corpo truncado, conexão recusada, deadline, cancelamento, leitura grande, redirect e CPU limitada. Depois executar teste de duração progressiva (30 s, 2 min, 10 min) mantendo perfil e observando recursos. Memória após aquecimento deve permanecer dentro de orçamento e sem crescimento aproximadamente proporcional a requests. Medir tendências e objetos retidos; pequenas oscilações de GC não são vazamento.

Para distinguir causas: fixture rápida no mesmo host estabelece somente um baseline funcional; fixture em outra máquina isola parte da contenção; adicionar workers mantendo RPS testa o gerador; limitar rede/alvo em ensaios separados testa hipóteses. Comparar contagem do servidor e do motor, lembrando que o servidor pode receber chamada cujo cliente depois sofreu timeout.

## Minikube: plano de ensaios seguro

Minikube é laboratório de integração, não certificação de capacidade comercial. Reservar CPU/RAM para sistema, API Kubernetes, CP e PostgreSQL antes de workers. Antes de cada execução, somar requests/limits de Pods e conferir capacidade alocável e espaço de disco. Não supor que usar nós virtuais no mesmo notebook cria isolamento físico.

Perfil de partida: 1 worker, 100 RPS, 30 s, C=64, request/limit 1 CPU e 512 MiB. É permitido reduzir caso o host não tenha folga. Fixture isolada e explicitamente autorizada no modo laboratório, com recursos próprios e comportamento configurável; sem destino externo acidental. Progressão somente com aprovação dos critérios de saúde, em pequenos passos, nunca começar pelos cenários de 15k/50k.

Limiares experimentais de interrupção do ensaio: pressão de memória/nó, eviction/OOM, indisponibilidade da API Kubernetes, disco com menos de 20% livre ou CPU do host acima de 80% sustentada por 30 s. São critérios conservadores a calibrar; pode ser necessário parar antes. Se a métrica de saúde não estiver disponível, não escalar a carga sem observação alternativa. Throttling do worker é um resultado medido, e também impede chamar aquele cenário de “gerador não saturado”.

| Marco | Teste específico no Minikube |
|---|---|
| M1 | Não aplicável; matemática/transporte validados localmente |
| M2 | Banco com PVC, indisponibilidade e restore em instância separada |
| M3 | Restart do CP preserva sessão/plano; banco fora impede novos leases |
| M4 | Job/Secret/Policy lifecycle, rede negada, quotas, SIGTERM/SIGKILL e OOM |
| M5 | Pod atrasado, autorização parcial, perda de heartbeat, merge/retry e dois reconciliadores |
| M6 | SSE reconecta, estado parcial permanece claro, sessão revogada fecha stream |
| M7 | Falhas combinadas, cleanup pendente e recuperação de backup/chaves |

Ensaios de rede entre nós e relógios divergentes precisam de hosts independentes antes de alegar comportamento distribuído em produção. Não compensar limitação do laboratório introduzindo broker/OLAP ou alterando sem evidência a semântica do motor.

## Tecnologias auxiliares e justificativas

M1 usa bibliotecas base de .NET; a FICHA-001 histórica selecionou xUnit; fixture pode usar ASP.NET Core, parte da stack. Sem biblioteca de geração de carga ou histograma. No M2, Npgsql é o driver necessário para PostgreSQL; EF Core/provider PostgreSQL é candidato para catálogo/Identity, enquanto ingestão poderá usar SQL transacional explícito. A escolha de acesso a dados requer ficha própria e benchmark, sem repositório genérico obrigatório.

No M4, cliente Kubernetes mantido e compatível com a versão do cluster evita manter implementação manual da API; CNI com enforcement é necessário às políticas. Nenhum pacote é instalado por esta atualização documental. Ferramentas de benchmark sofisticadas só entram se cronômetro, métricas do runtime e ensaios reproduzíveis não responderem à questão.

## Estudos antes da primeira implementação

Prioridade: `async/await`, ownership/disposal de HTTP e cancelamento; tempo monotônico e diferença entre taxa e concorrência; open-loop e coordinated omission; contadores, histogramas e quantis; testes com relógio/HTTP controlados. Depois: transações/idempotência, leases/fencing, Jobs/RBAC/NetworkPolicy, recuperação de estado e SSRF. Referências oficiais estão nos documentos 02, 03, 05 e 07. Kubernetes não precisa ser dominado para concluir a primeira ficha local.
