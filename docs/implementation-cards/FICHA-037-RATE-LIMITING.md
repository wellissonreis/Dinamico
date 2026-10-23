# FICHA-037 — Identificação e diagnóstico de Rate Limiting

Prioridade: P0 para diagnóstico confiável. Complexidade: G no conjunto; entregas por marco. Marcos: M1–M6. Status: **etapas M1 e M2 concluídas no escopo local**, incluindo persistência dos indicadores sem dupla soma e consulta. Controle protegido, distribuição e UI de M3–M6 permanecem pendentes. Envelope HTTP condicionado ao host na 012; evidências de persistência nas 013–015. Data: 2026-10-22.

## Objetivo

Identificar limitações sinalizadas pela API e mostrar seu impacto na carga observada, sem confundir requisições enviadas, respostas HTTP e processamento de negócio. Preservar precisão, consumo limitado de recursos e segurança das credenciais.

## Registro histórico do recorte anterior e continuação (2026-10-12)

Antes deste recorte, somente a FICHA-001 estava implementada: contratos imutáveis, validação local e observação terminal em .NET 10, com **119 testes históricos** de 2026-10-09. O host é informativo. Não existem HTTP/fixture/scheduler/agregação/logs/relatório/CP/UI funcionais, nem persistência de execuções ou workers Kubernetes implementados.

Na entrega anterior, a autorização cobria somente extensão dos contratos, validação e parser puro Retry-After: modos, retry None, regra 2xx-v1, índice de header e declaração local. A seção final [Rate Limiting — contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12) fixa os contratos. Evidências estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md). Contratos/parser verificados: 309 testes aprovados e build Release sem avisos/erros; integrações HTTP permanecem pendentes. Não criar transporte, acumulador, serviços, migrations, telas ou novos ADRs neste recorte.

A continuação entrega a fixture 002 e integra parcialmente o transporte HTTP da 003; ver evidências em RATE-LIMITING-DELIVERY. FICHA-037 permanece M1 parcial: transporte ainda precisa ser ligado ao motor, e agenda/contadores/janelas/logs/relatório e o gate 012 continuam pendentes. Os requisitos seguintes são integrações futuras, salvo contratos locais v2 explicitamente autorizados.

## Contexto arquitetural

Preservar [arquitetura modular](../02-SYSTEM-ARCHITECTURE.md), [semântica do motor](../03-LOAD-ENGINE-DESIGN.md), [persistência](../04-DATA-ARCHITECTURE.md), [segurança](../07-SECURITY.md) e [roadmap](../08-DEVELOPMENT-ROADMAP.md).

Aplicam-se ADRs 001–010, especialmente [open-loop](../adr/ADR-008-OPEN-LOOP.md), [segurança do destino](../adr/ADR-009-TARGET-SECURITY.md) e [resultados parciais](../adr/ADR-010-FAILURE-SEMANTICS.md). Manter workers .NET independentes, Kubernetes Jobs, PostgreSQL, REST/SSE, concorrência limitada, agregação local e histogramas combináveis. Nenhum serviço adicional é necessário.

## Conceitos necessários

429 é evidência de limitação sinalizada pelo caminho HTTP; não identifica sozinho se a origem é gateway, WAF, proxy ou aplicação. Ausência de 429 não prova ausência de limitação. IP compartilhado e credencial podem definir o escopo da política; atribuir a causa exige evidência do cliente ou fixture controlada.

## Responsabilidades e políticas

- **Normal (default):** executar o perfil configurado sob as políticas existentes.
- **Autorizado:** usar credenciais de teste fornecidas pelo cliente e vinculadas à execução. Em M1, exigir TestCredentialHeaderIndex válido e TestAuthorizationDeclared=true; em M3, integrar referências versionadas de credencial. A declaração local não é grant nem prova de autorização. Não presumir desativação de limites; na evolução remota, grants, quotas e segurança continuam obrigatórios.
- **Diagnóstico:** executar o mesmo perfil e destacar evidências, cobertura e limitações de interpretação. Não realizar sondagens extras nem adaptar RPS.

Na integração futura, todos os modos contabilizarão 429 e exibirão diagnóstico quando houver evidência. Autenticação também pode ser configurada em Normal/Diagnóstico; não é exclusiva do modo Autorizado.

## Fora do escopo

Bypass por header arbitrário, alteração da infraestrutura do cliente, rotação automática de IPs, proxies de evasão, inferência de capacidade real sob interferência e classificação automática de todo 4xx/5xx como Rate Limiting. Não introduzir closed-loop, fila ilimitada ou reposição de workers perdidos.

## Dependências e entregas por marco

| Etapa | Fichas relacionadas | Entrega quando os componentes existirem |
|---|---|---|
| M1 — motor local | 001–012 | Concluído no escopo HTTP/1.1 loopback; observabilidade, CLI e calibração aceitas na 012 |
| M2 — persistência | 013–015 | Concluído para importação local: janelas versionadas, transação idempotente, resumo/consulta e retenção dos indicadores |
| M3 — controle protegido | 018–022 | Referências de credenciais, revisão imutável, autorização e contratos REST |
| M4/M5 — distribuição | 023–028 | IPs de saída conhecidos, evidência de NAT, consolidação e falhas parciais |
| M6 — interface | 029–032 | Configuração React, SSE e relatório final |

O ID 037 é transversal, não significa esperar a 036 para começar nem antecipar os marcos futuros. Detalhar e integrar cada etapa apenas quando suas dependências estiverem disponíveis.

## Contratos locais v2 e integrações futuras

### Configuração e credenciais

No recorte local, configuração/plano fixam RateLimitMode Normal (default), Authorized ou Diagnostic; LoadRetryPolicy.None e HttpSuccessRule.Completed2xxV1. Sucesso exige HttpCompleted e status 200–299, inclusive 202, sem comprovar negócio. Rejeitar enums desconhecidos, sem fallback. Plano com `SchemaVersion = 2`, sem dados persistidos a migrar; futuros consumidores rejeitam schemas desconhecidos.

Headers carregam valores somente em memória. TestCredentialHeaderIndex opcional identifica o header de teste no array; índice inválido ou valor vazio é recusado. Authorized exige esse índice e TestAuthorizationDeclared=true; Normal/Diagnostic também aceitam credenciais. A declaração não remove limites nem substitui grant; não verifica emissão, propriedade ou validade da credencial. Referências versionadas e cofre são integração futura de M3, não requisito local.

Suportar API key em header configurável, Bearer Token, headers personalizados e credenciais específicas de teste. Resolver colisões de headers sem distinção de caixa com erro seguro, nunca sobrescrever silenciosamente Authorization. Reutilizar limites e validação existentes de headers, CRLF e headers proibidos.

Em M3, aplicar o cofre lógico já previsto em 07: cifrar valores persistidos, entregar ao worker somente os segredos necessários, por TLS autenticado, e manter apenas referências nos snapshots públicos. UI mostra máscara/indicador de configuração e permite substituição sem devolver o valor salvo. Tratar todos os valores de headers como sensíveis, inclusive nomes não convencionais. Não serializar planos/configurações completos em logs, SSE, erros ou relatórios. Revogação mantém a semântica de interrupção existente.

No recorte local, manter loopback, validação pura e compatibilidade explícita de schema; não criar cofre. Conforme [07](../07-SECURITY.md), HTTP com credenciais sintéticas em loopback é permitido no laboratório. Usar somente segredos sintéticos nesses testes; o validador não distingue segredos reais de sintéticos. HTTPS permanece obrigatório para o SaaS remoto.

### Contadores e denominadores — 008–011 implementadas

| Indicador | Definição |
|---|---|
| RPS planejado | Taxa do perfil original no intervalo; não recalculada após falha |
| RPS iniciado | Delta de `started` dividido pela duração do intervalo |
| RPS concluído HTTP | Delta de `http_completed` / duração; inclui 4xx/5xx com corpo completo |
| RPS aceito pelo critério HTTP | Delta de `http_success` / duração; corpo completo e status na regra publicada; nunca prova processamento de negócio |
| 429 observado | `status_received[429]`, contado uma vez ao receber status final, mesmo se o corpo falhar depois |
| Percentual limitado | `100 × status_received[429] / soma(status_received)`; denominador de respostas finais observadas, não de inícios; sem respostas: null |
| Distribuição HTTP | Contadores previstos por status final; somente 200–599; slots 100–199 ficam zero e 101/upgrade não é suportado no transporte local |
| Latência | Histogramas previstos de duração terminal e HTTP completo, com população e cobertura explícitas |
| Throughput | Respostas HTTP completas/s e bytes de corpo observados/s, com unidades e intervalo publicados |

Reutilizar `http_success`; não criar um contador concorrente chamado accepted com semântica distinta. Não permitir que 429 componha o critério de aceitação desta funcionalidade. Um 429 com timeout no corpo incrementa status 429 e timeout, mas não `http_completed`: são dimensões diferentes, não dupla contagem terminal.

Preservar as identidades de 03, as categorias de não iniciadas e os desfechos terminais exclusivos. O contador de 429 é derivado da distribuição existente. Não somar percentuais nem calcular média de percentis: somar numeradores/denominadores e buckets compatíveis antes de derivar resultados.

Eventos pertencem às respectivas janelas de registro: início, headers e conclusão podem cair em intervalos diferentes. Publicar duração de cada janela, incluindo drain e última janela parcial. Na visão global usar o mesmo intervalo temporal para todos os workers; não somar durações de workers nem dividir só pelo tempo dos sobreviventes. Cobertura ausente permanece desconhecida.

### Retry-After e retries

Contrato autorizado: RequestObservation tem status final 200–599; default continua Unknown. IsRateLimited deriva exclusivamente de 429, inclusive após falha de leitura, timeout ou cancelamento. 503 com Retry-After não ativa o sinal. Um 429 sem observação explícita mantém RetryAfter NotObserved; Missing exige captura efetiva sem campo. NotObserved não comprova ausência do header. Anexar evidência aplicável a outro status, ou NotApplicable a 429, é erro de contrato.

RetryAfterObservation é imutável e não retém texto bruto. Kind: NotObserved (default, captura não realizada), NotApplicable (status diferente de 429), Missing, Invalid, DeltaSeconds, HttpDate; Delay nulo nas quatro primeiras categorias. Para datas, ReferenceUtc é fornecido pelo chamador nos headers e ClockUncertain=true explicita relógio civil sem sincronia comprovada. Datas passadas viram zero. Teto de duração: 86.400 s, com WasClamped.

Parser puro recebe status, lista de valores brutos e UTC do chamador; só avalia 429. Lista vazia após captura efetiva = Missing; múltiplos = Invalid, sem concatenar/split por vírgula. Campo ≤128 caracteres antes de trim SP/HTAB. Delta só admite dígitos ASCII não negativos; overflow satura ao teto, mas toda a sintaxe deve ser verificada. Data aceita os formatos HTTP do protocolo. Sem leitura de relógio, I/O, espera ou alteração de plano; texto bruto ausente também dos erros. Header inválido não altera desfecho HTTP.

O callback de headers da 003 entrega a observação antes do corpo; a 008 agrega contagens fixas por categoria (incluindo NotObserved, sem contabilizá-lo como Missing), clamp/incerteza e min/max de Delay válido (null sem valores), junto ao registro do status. Merge soma contagens com overflow verificado e usa min/max existentes, em codec/janela versionados; sem labels por valor, texto bruto ou listas de observações. A 011 apresenta o resumo. Nenhum desses componentes é entregue pelo parser.

LoadRetryPolicy.None é explícita no plano e futuro relatório; 429/Retry-After não mudam RPS, concorrência ou agenda. Outras políticas são recusadas. Retry de telemetria é independente de retry de carga. Preservar ADR-008 sem alteração.

Retries de carga habilitáveis ficam como evolução pendente, exigindo revisão arquitetural: orçamento finito, identidade de tentativa, contabilização separada, método/idempotência, timeout e preservação do perfil open-loop. Não anunciar suporte a retry configurável habilitado nesta entrega.

### IPs públicos de saída — somente M4/M5 (futuro)

Representar conjunto deduplicado de IPs conhecidos por escopo de egress/worker, origem da informação (configuração do operador ou observação controlada), instante de atualização e estado conhecido/desconhecido. IP de Pod/nó não comprova IP público de saída; NAT pode reunir vários workers no mesmo endereço.

Inicialmente usar inventário configurado pelo operador. Se existir verificação, deve ocorrer em preparação, com destino fixo aprovado e tráfego separado das métricas de carga; não abrir acesso arbitrário a serviços de descoberta. Informação deve indicar limites de validade e possível variação por destino/rota.

Exibir IPs disponíveis para o cliente configurar allowlist na própria infraestrutura. Não garantir exclusividade, estabilidade ou dispensa de Rate Limiting. Sem inventário, mostrar desconhecido e manter a execução sujeita aos demais gates existentes.

## Diagnóstico e interface — futuro (CLI em M1; React/SSE em M6)

React deve oferecer seletor de modo, seleção/substituição de credencial e editor simples de headers. Mostrar a política sem retry, os IPs conhecidos e a regra de aceitação HTTP. Durante execução e no relatório, apresentar planejado/iniciado/concluído/aceito, 429, percentual com denominador, status, Retry-After, latência, throughput e cobertura.

Diagnóstico determinístico: pelo menos um 429 observado indica Rate Limiting sinalizado; sem 429, informar ausência de evidência por esse status; cobertura insuficiente torna a avaliação inconclusiva. Limitação do gerador e perda de worker aparecem separadamente e podem coexistir.

Exemplo ilustrativo para uma mesma janela de 1 s, com todas as respostas concluídas:

```text
Carga planejada:             15.000 req/s
Carga iniciada:              14.982 req/s
Respostas HTTP concluídas:   14.982 req/s
Aceitas pelo critério 2xx:    4.103 req/s
Respostas HTTP 429:          10.879 req/s
Requisições não iniciadas:       18

Diagnóstico: Rate Limiting detectado por respostas HTTP 429.
A capacidade real da aplicação não pode ser determinada
por este teste enquanto essa limitação interferir na carga.
```

Os valores não representam capacidade medida do projeto nem os limites locais atuais. 2xx, inclusive 202, não comprova conclusão do processamento de negócio. Resposta rápida de rejeição pode reduzir o percentil global; apresentar a composição por status junto à latência, sem chamar essa medida de latência interna da aplicação.

## Componentes e arquivos afetados na execução futura

- `src/Dinamico.LoadEngine/Contracts/LoadTestConfiguration.cs`, `LoadTestPlan.cs`, `RequestObservation.cs`, `ConfigurationError.cs` e `Validation/LocalPlanValidator.cs`: extensão mínima/versionada autorizada no recorte v2; evidências a cargo do coordenador.
- Transporte, agregador, fixture, testes e CLI previstos nas fichas 002–012: observação, contagem, redaction e apresentação.
- `docs/03-LOAD-ENGINE-DESIGN.md`, `04-DATA-ARCHITECTURE.md`, `06-OBSERVABILITY.md`, `07-SECURITY.md` e `08-DEVELOPMENT-ROADMAP.md`: integrar os contratos aprovados na etapa correspondente, evitando fontes normativas divergentes.
- Control Plane, persistência, workers, manifests e React: componentes futuros; seus caminhos serão definidos nos respectivos marcos, sem criar esqueletos agora.

## Passos do recorte e integrações futuras

1. Integrar contratos/validação/parser puro ao schema local 2 conforme a seção final de 03; registrar evidências reais em RATE-LIMITING-DELIVERY pelo coordenador.
2. Fixture 002 entregue; 003 captura status/Retry-After na observação terminal, 004 preserva duração monotônica, 005 mantém agenda open-loop e 006 limita admissão. Histograma/codec 007 entregue. A 008 expõe o evento de headers separado da conclusão terminal e implementa o acumulador; ligação ao ciclo implementada na 009.
3. A 008 agrega eventos, denominadores e cobertura; o dono de cada operação deve garantir registro único. A 009 liga o ciclo; futuramente, 010 verifica sigilo/custo; 011 apresenta relatório sanitizado; 012 fecha o gate integrado de M1.
4. M2/M3 adicionam persistência e credenciais protegidas; M4/M5 verificam NAT/egress e consolidação; M6 entrega UI/SSE. Cada etapa depende de evidência própria.

Coordenação desta entrega exclusivamente por tmux, com modelos/esforços conferidos e ownership por arquivo. Registro em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md). Nas próximas etapas, revalidar estágio e modelos disponíveis, fixar semânticas antes de delegar, preservar ownership, revisar integrações e registrar testes reais. Não criar worktrees salvo conflito concreto.

## Testes obrigatórios — critérios, não resultados

| Cenário | Evidência esperada |
|---|---|
| API sem limites | Zero 429; contadores conciliados; não afirmar capacidade ilimitada |
| 429 contínuo e intermitente | Contagem exata; taxa de geração invariável; sem retry implícito |
| Outros 4xx/5xx, inclusive 503 com Retry-After | Não classificar automaticamente como Rate Limiting |
| 429 seguido de falha/timeout de corpo | Status preservado e um único desfecho terminal |
| Retry-After (recorte puro) | NotObserved default sem captura, NotApplicable em outros status, Missing somente após captura efetiva sem campo e associações inválidas; segundos ASCII, zero, formatos HTTP de data futura/passada, malformado, duplicado, campo >128 antes do trim, overflow com sintaxe inválida posterior, clamp e incerteza; recursos limitados |
| Configuração (recorte puro) | Schema 2/defaults/enums desconhecidos, credenciais nos três modos, índice inválido/vazio e declaração exigida em Authorized; erros sem canários |
| Aceitação e janelas | 2xx com corpo incompleto não aceito; regra versionada; fronteiras/drain e denominador zero |
| Limite por IP compartilhado (somente M4/M5) | Fixture observa origem comum de múltiplos workers; relatório não confunde workers com IPs distintos |
| Limite por credencial | Fixture com políticas diferentes; modo autorizado ainda contabiliza 429 |
| Falha de worker (M5) | Parada e resultado parcial conforme ADR-010; sem reposição, extrapolação ou zeros fictícios |
| Agregação distribuída (M2/M5) | Replay de janela não duplica; percentuais ponderados, merge de buckets e cobertura corretos |
| Sigilo | Segredos-canário ausentes em logs, erros, SSE, relatórios e respostas de edição; isolamento e revogação |
| Custo | Nenhum log por request, cardinalidade fixa e buffers dentro dos tetos existentes |

Registro histórico das continuações 001–003: no recorte anterior, contratos/validação e parser puro totalizaram 309 testes aprovados. A continuação 002 adicionou 56 casos da fixture (365 no total); a continuação 003 adiciona 9 testes do transporte, elevando a suíte a 374 aprovados, zero falhas/ignorados. Testes e comportamento do scheduler, ciclo local, agregação, observabilidade, relatório e gate integrado continuam pendentes nas fichas 004–012. Testes distribuídos e de UI dependem de M4/M5 e M6. Não simular entrega Kubernetes apenas com teste unitário.

Registro da continuação 007 em 2026-10-17: deadline 004, agenda 005 e admissão 006 já implementados (455 testes históricos de 13/10); histograma/codec 007 agora validado com 17 direcionados e suíte de 472 testes, build Release sem avisos/erros. Evidências na [FICHA-007](FICHA-007-HISTOGRAM.md) e em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md). Janelas 008 e fichas 009–012 continuam pendentes. Expor recebimento de headers independentemente da conclusão terminal é pré-requisito para status na janela correta. M1 e FICHA-037 permanecem incompletos.

Registro da continuação 008 (2026-10-17): callback de headers separado do terminal, contadores/janelas imutáveis, sink limitado e codec/consolidação local implementados. **24 casos novos, 87 direcionados e 496 na suíte completa**, build Release sem avisos/erros. Evidências e limitações na [FICHA-008](FICHA-008-METRIC-WINDOWS.md). A 009 ainda deve integrar geração/drain/flush; M1 e FICHA-037 seguem incompletos.

Continuação histórica 009 (2026-10-17): ciclo local integrado, cancelamento/drain, retenção até ACK e flush limitado, razão/qualidade/completude independentes. **19 casos novos, 97 direcionados, 515 na suíte completa**, build Release sem avisos/erros. Dois cenários de corrida repetidos dez vezes: 20/20 aprovados. Evidências na [FICHA-009](FICHA-009-LOCAL-LIFECYCLE.md). 010–012 e M1 integrado permanecem pendentes; a FICHA-037 segue M1 parcial.

## Critérios de aceite

429 é contabilizado sem alterar agenda ou populações existentes. Relatório explicita regra de aceitação, denominadores, cobertura e incerteza sobre capacidade real. Segredos permanecem protegidos e não existe mecanismo de evasão. Cada etapa tem evidência reproduzível; a ficha só fica integralmente pronta após M6.

## Riscos e trade-offs

Não é possível localizar o componente limitador apenas pelo status; políticas podem usar outros sinais. IP conhecido não garante rota estável. Histogramas gerais misturam respostas aceitas e rejeitadas; separar novas populações só após justificar custo e versionamento. Preservar simplicidade exclui retries de carga habilitados desta primeira entrega.

## Definição de pronto

- [x] Contratos locais revisados e incorporados aos documentos canônicos; etapas futuras identificadas.
- [x] M1 parcial: contratos/validação/parser puro v2 implementados e verificados (309 testes na suíte).
- [x] Fixture HTTP local 002 e seus cenários verificados na continuação.
- [x] Transporte HTTP 003 implementado e verificado com a fixture local.
- [x] M1 integrado: transporte, agenda, contadores, logs e relatório verificados; aceite condicionado ao envelope da 012.
- [ ] M2/M3: persistência, credenciais e contratos protegidos verificados.
- [ ] M4/M5: egress, NAT, agregação e falhas distribuídas verificados.
- [ ] M6: configuração, acompanhamento e relatório React verificados.
- [x] Registro desta etapa informa decisões, arquivos alterados, implementação versus documentação, testes e pendências.

## Registro histórico da elaboração inicial (2026-10-12)

Entregue somente planejamento: esta ficha e sua entrada no índice. Nenhum código executável, ADR vigente ou schema persistido foi alterado. Revisão estática contra README, fichas e contratos existentes; testes automatizados não executados porque não houve alteração funcional. Os 119 testes citados na FICHA-001 são evidência histórica, não resultado desta tarefa.

Próximo passo histórico: revisar a proposta e incorporar o recorte local às fichas de M1. Atualização atual: recorte autorizado e integrado à documentação; contratos/parser implementados e verificados, demais entregas mantidas nos respectivos marcos. O diretório não é reconhecido como repositório Git neste ambiente; não foi possível obter diff/status por Git.

## Continuação atual 010–012 — 2026-10-17

Observabilidade limitada e relatório CLI foram integrados, com testes reais em subprocesso para os três modos, 202/429/503, headers separados de terminal, corpo limitado/timeout, canários, sinais e falhas de saída. Guia atual no [README](../../README.md), evidências e resultados na [012](FICHA-012-ENGINE-VALIDATION.md). Os registros anteriores do host informativo/011 futura são históricos. **M1/037 permanecem parciais**: os ensaios locais revelam drops por atraso e não fecham o gate sem saturação; faltam calibração com repetições, recursos prolongados, transporte remanescente e marcos posteriores. Não foi alterado nenhum critério de aceite para acomodar os resultados.
