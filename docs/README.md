# Base arquitetural — SaaS de testes de carga

Estado atual (2026-10-12): contratos/validação v2 e parser Retry-After implementados (001/037). A continuação adiciona a fixture HTTP loopback da FICHA-002 e seus testes. FICHA-037 permanece **M1 parcial**: transporte do motor, scheduler, agregação, relatório, Control Plane e UI pendentes. Evidências e limites em [RATE-LIMITING-DELIVERY](RATE-LIMITING-DELIVERY.md).

Fonte normativa do recorte: seção final [Rate Limiting — contrato local v2](03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12). Plano `SchemaVersion = 2`, modos Normal/Authorized/Diagnostic, retry None e `HttpSuccessRule.Completed2xxV1`. Credenciais em Headers são permitidas nos três modos; Authorized exige índice e declaração local, sem substituir grants. Evidências estão em [RATE-LIMITING-DELIVERY](RATE-LIMITING-DELIVERY.md). Validação atual: 365 testes aprovados e build Release sem avisos/erros; os 119 testes são históricos.

RetryAfterKind tem default NotObserved (captura não realizada); NotApplicable identifica status diferente de 429, e Missing exige captura efetiva em 429 sem campo. Não inferir ausência do header a partir de NotObserved. Conforme [07 — segurança](07-SECURITY.md), HTTP com credenciais sintéticas em loopback é permitido no laboratório; o validador não verifica emissão, propriedade ou validade da credencial nem distingue segredos reais de sintéticos. Usar somente segredos sintéticos nesses testes; HTTPS e os controles remotos permanecem requisitos de M3.

Registro histórico (2026-10-08): proposta v0.1 para revisão, sem autorização de implementação naquela entrega.

## Ponto de partida histórico (2026-10-08)

A inspeção encontrou o diretório de trabalho sem arquivos de aplicação ou documentação. Os diretórios reservados `.git`, `.agents`, `.codex` e `.aws` estão presentes, mas não há um repositório Git reconhecido por `git status`. Não foram inicializados Git, solução .NET, dependências ou infraestrutura. Não havia `AGENTS.md` aplicável nos diretórios ancestrais examinados.

## Leitura recomendada

1. [Visão do produto](01-PRODUCT-VISION.md).
2. [Arquitetura do sistema](02-SYSTEM-ARCHITECTURE.md).
3. [Projeto do motor](03-LOAD-ENGINE-DESIGN.md).
4. [Persistência e dimensionamento](04-DATA-ARCHITECTURE.md).
5. [Ciclo de vida](05-EXECUTION-LIFECYCLE.md).
6. [Observabilidade](06-OBSERVABILITY.md).
7. [Segurança](07-SECURITY.md).
8. [Roadmap](08-DEVELOPMENT-ROADMAP.md).
9. [ADRs](adr/README.md) e [fichas de implementação](implementation-cards/README.md).
10. [Revisão de consistência e questões abertas](REVIEW.md).

## Convenções e fontes de verdade

- **Confirmado:** requisito fornecido pelo proponente, incluindo a stack baseline; a proibição de implementar em 2026-10-08 é histórica.
- **Proposto:** decisão arquitetural fundamentada, ainda sujeita à revisão conjunta. ADRs têm status Proposto.
- **Hipótese:** valor inicial para experimentos, sem alegação de capacidade medida.
- **Pendente:** escolha que depende de medição ou decisão de produto. Uma pendência de segurança bloqueia a fase que expõe o risco, não pode ser ignorada.

O documento 03 define a semântica dos contadores e histogramas; 04 define persistência e retenção; 05 define estados e protocolo; 07 define política de segurança. Os demais referenciam essas definições. Alterá-las exige revisar os consumidores e o ADR associado.

RPS solicitado é a taxa de chegadas planejadas; RPS iniciado é a taxa de chamadas admitidas no transporte; RPS concluído mede respostas com corpo completamente consumido. Nenhum deles equivale automaticamente a requisições vistas pelo servidor. **Control Plane** abaixo significa nossa aplicação, não os componentes de controle internos do Kubernetes. **Worker** é um processo; **slot** é sua parcela lógica da carga; **tentativa** identifica uma instância autorizada desse slot.

## Parâmetros iniciais compartilhados

Todos os valores desta tabela são hipóteses operacionais configuráveis, com validação de teto; não são benchmarks ou SLA.

| Parâmetro | Proposta inicial | Observação |
|---|---:|---|
| Janela de métricas | 5 s | Deltas imutáveis; última janela pode ser parcial |
| Heartbeat / polling de comando | 2 s | Separado da fila de métricas |
| Lease de geração de tráfego | 10 s | Renovação validada no PostgreSQL; expiração interrompe a carga |
| Preparação / fila | 120 s / 300 s | Inclui pull de imagem e admissão |
| Antecedência de início coordenado | 5 s | Todos precisam confirmar antes de T0 menos 1 s |
| Incerteza máxima do relógio | 50 ms | Barreira reprova acima disso; medir viabilidade |
| Atraso tolerado para início | 100 ms | Worker posterior não entra no teste |
| Timeout por requisição | 10 s; teto 60 s | Inclui conexão, headers e corpo |
| Drain normal | Até o timeout configurado | Cancelamento explícito aborta requisições em voo |
| Flush final | 10 s | Sem novas requisições; falha implica dados parciais |
| Finalização do Control Plane | Timeout da requisição + 20 s | Contado a partir do fim da carga/parada |
| Buffer de envio por worker | Até 6 janelas e 256 KiB, o menor | Inclui amostras; ao esgotar, parar carga |
| Erros amostrados | Até 5/janela e 100/worker/execução | Até 512 bytes por amostra sanitizada |
| Perfil inicial no Minikube | 100 RPS, 30 s, 1 worker | Subir apenas após aferir saúde do host/cluster |
| Tetos do host local M1 | 1.000 RPS, 600 s, C=256 | Default C=64; aumentar teto só em configuração do operador para calibração |

A revisão pode mudar esses valores, desde que atualize contratos e testes. Limites de RPS por worker serão obtidos por benchmark, nunca deduzidos apenas do número de CPUs.

Os tetos do host local impedem erros de entrada; não afirmam que a máquina consegue sustentá-los. São distintos das quotas do SaaS, a definir por calibração. Operador pode reduzir ou revisar esses tetos sem ampliar a política de destino loopback.

## Referências

As fontes oficiais consultadas estão próximas às decisões nos documentos. Data de consulta: 2026-10-08. Páginas `current` e documentação de Kubernetes podem evoluir: registrar versões exatas de runtime, cluster, CNI, PostgreSQL e cliente Kubernetes quando a fase correspondente começar. Referências sustentam comportamentos das plataformas; o desenho específico deste SaaS é uma proposta, não uma recomendação oficial dos fornecedores.
