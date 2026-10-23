# FICHA-001 — Contratos e limites do motor local

Prioridade: P0. Complexidade: P. Marco: M1. Status: base e extensão v2 implementadas; integração HTTP local validada no gate 012 de 2026-10-21. Registros anteriores são históricos.

## Objetivo

Uma configuração inválida deve ser recusada antes de abrir qualquer conexão; uma válida deve produzir um plano imutável e versionado.

## Contexto arquitetural

O mesmo motor será usado pela CLI e pelo worker. Seus contratos não dependem de ASP.NET, banco ou Kubernetes; ver [03](../03-LOAD-ENGINE-DESIGN.md).

## Conceitos necessários

RPS é taxa de chegada, C é limite de operações simultâneas. Snapshot imutável impede mudanças de sentido durante execução. Unidades explícitas evitam confundir segundos, ticks e milissegundos.

## Responsabilidades

Definir configuração local, plano, observação terminal, enumerações de erro/qualidade e limites validados. Definir identificadores e schema version. Organizar solução mínima de motor, host local e testes após autorização.

## Fora do escopo

Transporte, UI, persistência, HTTP/2, código distribuído e API remota.

## Dependências

Revisão M0: aprovar escopo loopback, semântica dos contadores e aproximação dos histogramas.

## Contratos

Entrada: método, URI loopback, headers limitados, payload estático ≤64 KiB, RPS inteiro positivo, duração inteira em segundos, C positivo, timeout ≤60 s e tamanho máximo de resposta. Saída: plano ou coleção de erros com códigos/campos seguros. O host local não aceita flag de liberar qualquer destino. `planned_total=R×D` usa overflow verificado.

## Extensão autorizada — contrato local v2 (2026-10-12)

Recorte da FICHA-037 sobre esta ficha: somente contratos, validação e parser puro Retry-After; sem aceite das integrações de M1. Fonte normativa: seção final [Rate Limiting — contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12). Evidências atuais estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md). Recorte verificado: 309 testes aprovados e build Release sem avisos/erros; 119 é o resultado histórico da base.

- Plano `SchemaVersion = 2`, sem dados persistidos a migrar; consumidores futuros rejeitam schemas desconhecidos.
- `RateLimitMode`: Normal (default), Authorized, Diagnostic; mesmo perfil, sem sondagens. `LoadRetryPolicy.None` é a única política. `HttpSuccessRule.Completed2xxV1` exige HttpCompleted e status 200–299, inclusive 202; não prova processamento de negócio. Rejeitar enums desconhecidos.
- Reutilizar Headers em todos os modos. TestCredentialHeaderIndex opcional deve apontar para header válido com valor não vazio; Authorized exige índice e TestAuthorizationDeclared=true. Declaração local não é grant nem verifica emissão, propriedade ou validade da credencial. HTTP com credenciais sintéticas em loopback é permitido no laboratório conforme [07](../07-SECURITY.md); usar somente segredos sintéticos nesses testes, pois o validador não distingue segredos reais de sintéticos. Preservar limites, colisões sem distinção de caixa e headers proibidos; segredos só em memória. Referências/cofre pertencem a M3.
- Status terminal somente 200–599; default continua Unknown. IsRateLimited deriva só de 429, inclusive após falha/timeout/cancelamento do corpo. 101/upgrade não é suportado; 1xx intermediário não é terminal. Um 429 sem observação explícita mantém RetryAfter NotObserved; Missing exige captura efetiva sem campo. NotObserved não comprova ausência do header. Evidência aplicável em outro status, ou NotApplicable em 429, é erro de contrato.
- RetryAfterObservation imutável, sem texto bruto: NotObserved (default, captura não realizada), NotApplicable (status diferente de 429), Missing, Invalid, DeltaSeconds, HttpDate; Delay nulo nas quatro primeiras categorias. Parser recebe status, lista de valores e UTC do chamador; sem relógio/I/O/espera. Só avalia 429: captura efetiva com zero valores = Missing, múltiplos = Invalid, sem concatenar ou dividir por vírgula. Campo ≤128 caracteres antes de trim SP/HTAB; delta só dígitos ASCII não negativos, com sintaxe integralmente verificada mesmo após overflow. Datas nos formatos HTTP, passadas = zero, ReferenceUtc e ClockUncertain explícitos; duração ≤86.400 s, com WasClamped.

Testes executados no recorte: defaults/schema/enums; índice negativo/fora do array/valor vazio; declaração ausente e credenciais nos três modos; 200/202 completos e 2xx incompleto; 429 após falha de corpo e 503 com Retry-After sem sinal; NotObserved default versus Missing após captura efetiva sem campo, NotApplicable em outros status e associações inválidas da observação; todos os formatos HTTP de data, campo longo antes do trim, duplicados, sinal/dígitos não ASCII, overflow seguido de caractere inválido, clamp e relógio incerto. Canários em headers/erros/ToString não podem vazar. Resultados registrados no documento de entrega vinculado acima.

Integração futura: 003 fornece status/valores/referência nos headers; 004 preserva observação após falha; 005 mantém agenda; 008 acumula; 010/011 sanitizam e apresentam; 012 verifica o conjunto. Não criar esses componentes nesta extensão.

## Decisões técnicas

.NET 10 e tipos simples imutáveis; escolher um único framework de testes, como xUnit, justificando apenas suporte a testes automatizados. Relógio como fronteira mínima para testes determinísticos; sem framework de regras, mediator ou repositório genérico.

## Passos de implementação

1. Fixar SDK/runtime e criar estrutura mínima separando motor/host/testes.
2. Especificar campos, unidades, defaults e tetos locais finitos; C inicial 64.
3. Validar host loopback literal ou resolução pinada, método, headers e tamanhos.
4. Definir erros e schemas sem carregar segredos em mensagens.
5. Registrar decisões que precisarem mudar a proposta antes das fichas dependentes.

## Testes obrigatórios

Aceitar configuração mínima; rejeitar negativos, zero indevido, overflow, URL remota, userinfo, CRLF, métodos proibidos, payload excessivo e timeout acima do teto. Testar que erro não chama transporte e que defaults aparecem no plano.

## Observabilidade

Evento `configuration.rejected` com código/campo; nunca valor de header, payload ou query. Plano registra versão e limites públicos.

## Critérios de aceite

Toda configuração aceita tem limites finitos e total planejado determinístico. As entradas negativas da suíte são recusadas sem I/O. Motor compila sem dependências de CP/DB/Kubernetes.

## Riscos e trade-offs

Limites locais são de segurança operacional, não envelope de performance. Evitar contratos genéricos para protocolos fora do escopo.

## Definição de pronto da base (histórico, 2026-10-09)

- [x] Contratos e unidades documentados e consistentes com 03.
- [x] Casos válidos/inválidos passam e nenhum segredo aparece em erro.
- [x] Dependências justificadas e nenhuma infraestrutura remota exigida.

## Evidências históricas da base (2026-10-09)

Implementação autorizada pelo proponente nesta conversa, limitada à primeira ficha. SDK 10.0.204, projetos `net10.0`, execução de testes no Windows via WSL com runtime .NET 10.0.12. As decisões, restrições concretas da entrada e o inventário de arquivos estão no [README da implementação](../../README.md).

| Critério | Evidência |
|---|---|
| Configuração válida produz plano finito e determinístico | Testes de defaults, mínimos/máximos, `planned_total` e overflow; plano com propriedades somente de leitura |
| Configuração inválida é recusada sem I/O | Validador puro, sem dependência de transporte/DNS/arquivo; testes negativos nunca recebem plano |
| Snapshot não muda com a entrada | Testes alteram arrays originais e novas cópias dos arrays imutáveis; plano permanece igual |
| Valores sensíveis não entram em erros | Canário em URL, método, header e payload ausente de erros serializados e representações textuais |
| Observação terminal preserva populações | HTTP 500 completo mantém duração HTTP; timeout após headers 200 não mantém; combinações inválidas são recusadas |
| Motor desacoplado | Projeto de biblioteca sem PackageReference ou FrameworkReference para ASP.NET, banco ou Kubernetes |

Suíte automatizada histórica: **119 testes aprovados, zero falhas e zero ignorados**, executados em Release com `dotnet.exe test Dinamico.slnx --configuration Release --no-restore --logger trx --results-directory TestResults`.

O evento `configuration.rejected` é representado nos erros por nome/código/campo seguros. Emissão de logs será feita pelo host na ficha apropriada; não foi introduzido logger no validador. A opção escolhida foi loopback literal, sem resolver `localhost` ou DNS. Testes de sockets reais e fixture pertencem à FICHA-002/003, não a esta entrega.
