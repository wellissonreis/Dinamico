# Revisão arquitetural e pauta de decisão

Estado atual (2026-10-12): contratos/validação v2 e parser Retry-After implementados (001/037). A continuação adiciona a fixture HTTP loopback da FICHA-002 e seus testes. FICHA-037 permanece **M1 parcial**: transporte do motor, scheduler, agregação, relatório, Control Plane e UI pendentes. Evidências e limites em [RATE-LIMITING-DELIVERY](RATE-LIMITING-DELIVERY.md).

A seção final de [03 — contrato local v2](03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12) prevalece sobre as propostas antigas: SchemaVersion 2, retry None, modos Normal/Authorized/Diagnostic e `HttpSuccessRule.Completed2xxV1`. Authorized exige TestCredentialHeaderIndex e TestAuthorizationDeclared; os demais modos também aceitam credenciais. É declaração local, não grant. Evidências registradas em [RATE-LIMITING-DELIVERY](RATE-LIMITING-DELIVERY.md).

RetryAfterKind tem default NotObserved (captura não realizada); NotApplicable identifica status diferente de 429, e Missing exige captura efetiva em 429 sem campo. Não inferir ausência do header a partir de NotObserved. Conforme [07 — segurança](07-SECURITY.md), HTTP com credenciais sintéticas em loopback é permitido no laboratório; o validador não verifica emissão, propriedade ou validade da credencial nem distingue segredos reais de sintéticos. Usar somente segredos sintéticos nesses testes; HTTPS e os controles remotos permanecem requisitos de M3.

Integração futura: 002/003 verificam 429 e 503 com Retry-After e falhas após headers; 004 preserva status e duração monotônica; 005 prova open-loop sem retry/adaptação; 008 verifica contagem única, denominadores, drain e cobertura; 010/011 verificam canários e apresentação sanitizada; 012 fecha o aceite integrado. NAT e egress são gates apenas de M4/M5; cofre/referências pertencem a M3. Não há novo ADR ou componente futuro criado pelo recorte.

## Registro histórico da revisão (2026-10-08)

Resultado: base documental concluída como **proposta para revisão**, sem código de produção, dependências instaladas ou infraestrutura criada. Não há benchmarks executados nesta entrega.

## Resumo executivo da proposta histórica

Proposta: Control Plane ASP.NET Core em monólito modular; PostgreSQL como fonte de intenção/coordenação/resultados; worker .NET separado e efêmero por Kubernetes Job. React usa REST/SSE. O primeiro marco isola o motor numa CLI local com fixture loopback; Kubernetes entra depois que taxa, latência, cancelamento e recursos forem verificáveis.

O CP gerencia identidade, catálogo, admissão, estados, integração Kubernetes e ingestão. Worker gera tráfego e agrega localmente, sem acesso ao banco. Scheduling/reconciliação chama a mesma máquina de estados, evitando duplicação de regra. A consulta de resultados é uma projeção do monólito, não outro serviço.

Decisões centrais: agenda open-loop com concorrência limitada e drops explícitos; partição fixa de chegadas entre W slots; barreira com T0 futuro e leases; deltas idempotentes com ACK após commit; histogramas combinados por bucket; parada perante perda de worker/controle; autorização de destino e SSRF como gate remoto. Dez ADRs registram motivos e condições de revisão.

## Revisão de consistência

| Área | Verificação e ajuste |
|---|---|
| Escopo | Separados marco local, MVP SaaS e evolução; único endpoint por teste explicitamente proposto |
| Responsabilidades | CP decide estado; reconciliação solicita efeitos; Kubernetes observa processo; worker não escreve estado autoritativo |
| Agenda | Taxa global é plano de chegadas, não promessa de envio físico; sem catch-up ilimitado nem reposição de worker |
| Tempo | UTC para coordenação; monotônico para intervalos; quantização/jitter explícitos |
| Contadores | Início/conclusão podem cruzar janelas; identidades são finais, com incompletude explícita em crash |
| Rotação concorrente | Atribuição à janela usa registro serializado; duração I/O usa marcos próprios, sem reabrir janela fechada |
| Percentis | Populações HTTP completo/terminal separadas; merge por buckets; null/overflow explícitos |
| Cobertura | Manifesto final prova última janela; workers já encerrados não geram falsas lacunas de drain |
| Banco | Dedup e merge na mesma transação; UInt64 do formato limitado à faixa de bigint do banco |
| Dimensionamento | Estimativa revisada para 20 KiB por linha, incluindo array de 501 status; tabelas/fórmulas recalculadas |
| Parada | FINALIZING separa intenção e comprovação; timeout administrativo não libera reserva de processo desconhecido |
| Reconciliação | Varredura de órfãos cobre efeitos Kubernetes em trânsito após cancelamento/lease perdido |
| Credenciais | Configuração sensível cifrada uma vez; snapshot usa referências; resultados nunca contêm valores |
| Segurança | Autenticação, quotas, grants e bloqueios de rede antecedem execução remota; modos de laboratório separados |
| Roadmap | Métricas e recuperação iniciam cedo; M7 endurece, sem esconder dependências críticas até o final |
| Fichas | 12 fichas com formato obrigatório, prioridades e complexidades; dependências formam grafo acíclico |

Verificações documentais incluem existência dos links locais, seções obrigatórias das fichas/ADRs, blocos de código balanceados e cálculos de armazenamento. Diagramas Mermaid foram revisados textualmente; não houve renderização gráfica nem validação em cluster. Testes de software citados são critérios para implementação futura, não evidências de comportamento já implementado.

## Riscos prioritários e resposta

| Risco | Resposta proposta | Evidência ainda necessária |
|---|---|---|
| Gerador saturado parecer alvo saudável | Drops, lateness, perfis de recurso e flags | Calibração M1 com alvo rápido/lento |
| Quantis pouco precisos ou enviesados | Erro logarítmico declarado e populações separadas | Vetores determinísticos e aceite de 2,19% |
| Corrida/partição gerar carga duplicada | Registro exclusivo, leases e ausência de substituição | Falhas injetadas M4/M5; hosts separados |
| Abuso/SSRF | Prova + aprovação, IP pinado, quotas e egress | Suíte adversarial e CNI real antes de remoto |
| Perder métricas ou somar retry | Buffer limitado, parada e dedup transacional | ACK perdido, crash e pressão de ingestão |
| CP/PG competirem com carga no Minikube | Reserva de recursos e progressão conservadora | Monitoramento do host e limites de interrupção |
| Chave/secrets se perderem no restart | Key ring protegido/persistido e restore | Ensaio de backup/chaves antes de SaaS |

## Questões abertas e momento de decisão

| ID | Pergunta / hipótese proposta | Bloqueia | Como resolver |
|---|---|---|---|
| Q01 | Um endpoint e perfil constante bastam para o marco local? Proposta: sim | Início M1 | Revisão de escopo pelo proponente |
| Q02 | Erro de percentil ~2,19% é aceitável com N/população explícitos? Proposta: sim | Histograma 007 | Validar requisito; se não, comparar HDR em ADR substituto |
| Q03 | Tetos locais 1.000 RPS/600 s/C=256 e fixture loopback são apropriados ao host? | Calibração 012 | Inventário e medições; reduzir tetos se necessário |
| Q04 | Um endpoint por teste atende ao MVP? | Editor/contrato SaaS | Conversa com primeiros usuários; revisar cardinalidade se mudar |
| Q05 | CNI/driver e versões exatas do Minikube/Kubernetes | M4 | Provar NetworkPolicy e comportamento de NAT no ambiente |
| Q06 | Quem aprova destinos e quais provas adicionais para CDN? | Execução remota | Definir operação por convite e critérios de autorização |
| Q07 | Custódia da chave de proteção e backup cifrado | Credenciais remotas/produção | Definir operador/local de custódia e executar restore |
| Q08 | Quotas SaaS, capacidade por perfil de worker e SLA | Abertura do MVP | Medir M1/M4/M5; aprovar limites por destino/workspace |
| Q09 | Retenções 7/30/90 dias e retenção de backups | Histórico/produção | Confirmar necessidade do produto e obrigação contratual |
| Q10 | Limiares de início 50 ms de incerteza/100 ms de atraso são viáveis? | M5 | Ensaios entre hosts e relato explícito de qualidade |
| Q11 | ORM/acesso a dados e cliente Kubernetes específicos | M2/M4 | Avaliar pacotes/versionamento nas fichas correspondentes |

Q03 e Q08 são capacidade desconhecida, não lacunas a preencher inventando números. Q05–Q07 são gates concretos de segurança/operação; desenvolvimento local pode avançar após revisão sem resolvê-los antecipadamente. Nenhuma questão impede concluir a base documental atual.

## Primeiras fichas e estudo

Ordem recomendada: 001 contratos → 002 fixture → 003 transporte → 004 tempo/deadline → 005 agenda → 006 concorrência → 007 histograma → 008 janelas → 009 ciclo local → 010 observabilidade → 011 CLI → 012 calibração. O índice histórico registra dependências reais e itens futuros até 036; a atualização de 2026-10-12 inclui a transversal 037, sem detalhá-los prematuramente.

Antes da primeira implementação, estudar async/await e descarte, HttpClient/pool/streaming, cancelamento, tempo monotônico, open-loop versus concorrência, percentis/histogramas e testes determinísticos. Leitura sugerida: 01 para objetivo, 03 para motor e 001 para primeiro passo. Transações, leases e segurança Kubernetes vêm antes dos marcos que dependem deles.

Próximo passo proposto: revisar Q01/Q02 e os contratos de 03 com o proponente. A aprovação e autorização para implementar serão uma atividade posterior; esta entrega não contém código de produção.
