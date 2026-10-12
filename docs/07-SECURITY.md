# 07 — Segurança

## Modelo de ameaça e gates

O risco central é transformar o SaaS em origem de tráfego abusivo ou acesso a serviços internos. Considerar usuário autenticado malicioso, destino que muda DNS, resposta HTTP hostil, vazamento de token, worker duplicado e separação incorreta de tenants. O worker executa apenas nossa imagem e configuração declarativa validada; não executa scripts, plugins ou código do usuário.

O marco local só admite loopback explícito para fixture controlada; validação resolve o endereço e conecta ao IP permitido, sem redirects/proxy. Essa política é um modo do host de desenvolvimento, não campo do teste. **Não haverá parâmetro público para desativar SSRF.** O modo laboratório do Minikube fica em instalação separada, sem acesso de clientes, permitindo somente a fixture identificada por operador. Produção rejeita esses modos na inicialização.

Gate para execução remota: login e autorização, quotas, prova/aprovação de destino, secrets protegidos, política de conexão, CNI que aplique egress, worker isolado e testes adversariais aprovados. Não adiar esses itens para depois do lançamento da UI.

## Autenticação e autorização

Proposta: ASP.NET Core Identity para contas locais por convite; cookie de sessão `Secure`, `HttpOnly`, `SameSite=Lax`, prefixo `__Host-`, sem Domain. Frontend e API na mesma origem, HTTPS. POST/PUT/PATCH/DELETE exigem antiforgery e validação de Origin; login também recebe proteção CSRF. CORS fechado por padrão, nenhuma credencial de sessão em localStorage. SSE usa a mesma sessão, com revalidação periódica e encerramento após revogação.

Usar o hasher e tokens de Identity, sem implementar criptografia de senha. Limitar login/recuperação por identidade e origem, resposta sem enumeração de conta, lockout e expiração. Convite e reset são tokens de uso único e prazo curto; enquanto não houver entrega de e-mail configurada, bootstrap e recuperação são operação administrativa local auditada, não endpoint público incompleto. Revogar sessões no reset, remoção de membership e suspeita de comprometimento. Proposta: ociosidade 30 min, limite absoluto 8 h, verificação da revogação no banco em requisições protegidas e em ciclos SSE. [Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity?view=aspnetcore-10.0).

Papéis propostos: owner/admin de workspace gerenciam participantes e quotas; member com acesso ao projeto edita/testa/cancela; viewer consulta resultados sanitizados. Secret pode ser usado por executor autorizado sem permissão de ler seu valor. Todos os handlers verificam workspace e project da entidade real; informar UUID de outro tenant não dá acesso. Workers não possuem sessão de usuário nem autorização de catálogo.

## Credenciais e configuração sensível

Guardar referências versionadas de credenciais nas revisões. Bearer, Basic e API key entram em cofre lógico no PostgreSQL, cifrados com ASP.NET Core Data Protection e purpose específico por workspace/project/credential/version. Proteção autenticada detecta troca de ciphertext entre escopos. O key ring será persistido e protegido em repouso por certificado/chave de proteção montada no CP, fora do banco e do Git. Replicas compartilham ring e proteção; sem isso, reinício ou scale-out quebra sessões/credenciais. [Configuração Data Protection](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).

Não presumir que Kubernetes Secret está cifrado só por ser base64. Restringir leitura, habilitar proteção em repouso no cluster de produção e definir custódia/backup da chave antes de lançar. Banco e chave comprometidos juntos permitem descriptografia; esta proposta não equivale a um HSM/KMS. Um serviço externo de chaves só entra se a operação justificar. [Boas práticas de Secrets](https://kubernetes.io/docs/concepts/security/secrets-good-practices/).

Payloads necessários à **configuração** são pequenos, cifrados e guardados uma única vez na revisão; não há cópia por requisição, log ou amostra. Query strings e valores de headers configuráveis também são tratados como sensíveis. Metadados de listagem mostram hostname aprovado, método e nomes seguros de campos, sem valores. Snapshot da execução referencia revisão e credential_version; reprodutibilidade não significa permitir consultar um segredo revogado. Credencial revogada impede nova execução e interrompe as ativas que a utilizam.

Na preparação, o CP entrega somente os valores daquela execução ao worker por TLS autenticado, após exclusividade da tentativa. Valores ficam em memória pelo tempo necessário; minimizar strings/cópias e zerar buffers mutáveis quando possível, sem prometer apagar cópias do heap gerenciado. Não entregar por argumentos do processo, nomes de recursos, annotations, ConfigMap ou logs. Bootstrap de worker usa Secret temporário montado como arquivo, contendo só token aleatório limitado; CP armazena hash, vincula tentativa e revoga ao encerrar. Limpeza idempotente remove Secret, Job e política temporária.

Rotação de chave exige preservar decriptação dos dados ainda retidos e ensaiar reproteção/restauração. Rotação de credencial cria nova versão e não muda teste já armado; revogação é uma ação distinta que interrompe seu uso. Valores devolvidos em APIs de edição são mascarados e não recuperáveis pelo navegador.

## Autorização de destino

Cada grant fixa hostname canônico exato, porta, esquema, projeto, prazo e teto de carga. Exigir desafio DNS TXT com nonce de uso único ligado ao workspace e expiração; não usar uma URL arbitrária de verificação que torne o CP outro vetor SSRF. Proposta de validade do grant: 7 dias, com revalidação antes de execução e revogação administrativa imediata. Sem wildcards no MVP.

Prova DNS demonstra controle do nome, não autorização sobre CDN ou infraestrutura compartilhada. MVP por convite requer também aprovação do operador e declaração de autorização/termos do responsável pelo destino. O operador pode exigir evidência adicional e limitar taxa global por destino em transação, somando todos os workspaces. Se essa operação não for viável, não abrir cadastro irrestrito. Domínios públicos populares, DNS rebinding e hostname apontado a terceiro não ficam automaticamente autorizados por autenticação do usuário.

Não permitir IP literal remoto no MVP. Portas iniciais 80 e 443, outras apenas aprovadas pelo operador com grant explícito. Credencial exige HTTPS. Métodos CONNECT e TRACE proibidos. Proibir override de Host, headers hop-by-hop, Proxy-Authorization e framing conflitante; Content-Length e transferência são controlados pelo transporte. Rejeitar CR/LF em nomes/valores de headers e userinfo na URL.

## SSRF, DNS e conexão efetiva

Validar scheme HTTP(S), hostname normalizado/IDNA, porta, tamanho, userinfo e grant em API e worker. Resolver A/AAAA, normalizar IPv4 mapeado em IPv6 e rejeitar todo conjunto com destino não permitido. Bloquear loopback, RFC1918, link-local, ULA, multicast, unspecified, faixas reservadas/não globais, CGNAT, redes de Pods/Services/nós, IPs públicos da própria plataforma e endpoints de metadados conhecidos. A lista de ranges precisa derivar de registro mantido e testes, não apenas três prefixos IPv4.

Na preparação, fixar conjunto aprovado de IPs públicos e registrá-lo como evidência operacional sem dados de credencial. No `ConnectCallback` do handler, conectar diretamente a um IP desse conjunto, nunca resolver novamente implicitamente depois de validar. Preservar hostname para Host/SNI e validação TLS; não substituir URL por IP. A callback é o ponto suportado para abertura personalizada de conexão. [API ConnectCallback](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0).

Durante a execução, novas conexões só usam o conjunto pinado; mudança legítima de DNS pode causar falha e exigir nova execução. Redirecionamento automático e proxy ambiente ficam desabilitados; 3xx não é seguido nem reencaminha credenciais. Revalidar na abertura de cada conexão que grant/lease local permanece válido. Reutilizar uma conexão já autorizada não exige consulta ao banco por request. Revogação chega pelo heartbeat ou expiração do lease.

Aplicar defesas de camada de aplicação e rede em conjunto; validação apenas no cadastro é insuficiente contra rebinding, redirects e formas alternativas de IP. [Referência OWASP SSRF](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html).

## Isolamento e NetworkPolicy

Namespace de workers sem banco ou CP. Default-deny de ingress/egress; liberar DNS somente para resolvers do cluster, canal TLS para serviço interno específico do CP e egress aos IPs/portas aprovados do slot. Policy por execução usa labels geradas pelo servidor; remover no cleanup. Egress permitido ao CP serve apenas ao cliente de controle fixo; cliente de carga continua proibido de direcionar tráfego para ele. NetworkPolicy é L3/L4 e não confere hostname/URL, portanto não substitui o `ConnectCallback`.

Políticas dependem do plugin de rede e podem ter particularidades de NAT/host. No Minikube, instalar/configurar um CNI com enforcement de NetworkPolicy (candidato: Calico, apenas como mecanismo necessário de rede), e provar bloqueios na prática. Política presente no YAML não basta. O CNI é necessidade concreta de isolamento da baseline Kubernetes, não um service mesh. [NetworkPolicy Kubernetes](https://kubernetes.io/docs/concepts/services-networking/network-policies/) e [Minikube](https://minikube.sigs.k8s.io/docs/handbook/network_policy/).

Workers com usuário não root, filesystem de imagem read-only, capabilities removidas, seccomp RuntimeDefault, sem privilege escalation, hostNetwork/hostPID/hostPath e sem token de ServiceAccount (`automountServiceAccountToken=false`). Diretório temporário limitado quando necessário. CPU/memória/ephemeral-storage limitados, imagem fixada por digest e sem entrada para comandos arbitrários. Não há permissão de kubectl/exec para usuários do SaaS.

## RBAC e quotas

ServiceAccount do CP: Role no namespace de workers para criar/ler/listar/observar/deletar Jobs, ler Pods e seu status/eventos, criar/deletar Secrets bootstrap e policies específicas. Sem cluster-admin, nodes, exec, attach, criação de namespaces ou acesso a secrets do namespace do CP/banco. O provisionamento inicial de namespaces/Role/Quota é tarefa Helm do operador, não poder do backend. RBAC não consegue restringir toda criação por prefixo; reduzir alcance pelo namespace e validação de templates, reconhecendo o risco de comprometimento do CP.

O reconciliador deletará apenas recursos com execution/slot/UID verificados. Templates não aceitam imagens, volumes, comandos, service accounts ou securityContext fornecidos pelo usuário.

Quotas persistidas: RPS e requests totais por execução/workspace/destino, duração máxima, número de workers, C, tamanhos de request/response, execuções simultâneas, tempo em fila e ingestão de métricas. Reservar atomically antes de QUEUED para impedir corrida entre réplicas. Hipótese de laboratório: uma execução, um worker e duração máxima de 10 minutos; aumentos passam pelo benchmark. Tetos SaaS dependem desse benchmark, mas validação e mecanismo de quotas são requisitos prévios.

## Auditoria e testes de segurança

Registrar login/lockout, convite, mudança de papel, grant aprovado/revogado, uso/rotação de credencial, início/cancelamento, recusa SSRF, alteração de quotas e ações do operador. IDs, ator, alvo lógico, horário e resultado; nenhum valor secreto. Retenção mínima proposta em 04.

Suíte obrigatória antes de tráfego remoto:

1. Usuário e worker de A tentam acessar execução, janela, SSE e secret de B; todas as rotas negam.
2. Host público resolve para privado, mistura A público/AAAA privado, IPv4-mapped IPv6, metadados, loopback e endereço da plataforma; nenhum socket de carga chega ao destino proibido.
3. DNS muda entre validação e conexão; conectar somente ao IP pinado. Redirect para privado é contabilizado e não seguido.
4. Proxy ambiente, Host malicioso, userinfo, CRLF, payload/corpo gigantes e headers de framing conflitante são rejeitados.
5. Token expirado/revogado, Pod duplicado e epoch incorreto não iniciam tráfego. Retry de janela não amplia permissões.
6. No Minikube, tentativa de acessar banco/API Kubernetes/metadados falha na rede; fixture aprovada e canal de controle funcionam. Testar após alterações de CNI, não só uma vez.
7. Backup restaurado com chaves corretas recupera secrets; sem chave, não recupera. Logs e relatórios nunca exibem segredo sintético.
8. Concorrência de reservas não ultrapassa teto; falha de DB impede admissão. Parada permanece disponível sob abuso da API.

Pendências concretas para o gate remoto: versão/CNI validado, ranges da implantação, custódia da chave e processo de aprovação de destinos. Nenhuma delas é liberação tácita para operar sem proteção.

## Recorte local de Rate Limiting — 2026-10-12

O contrato local usa os headers existentes para API keys, Bearer e credenciais de teste, com máscara integral nas representações textuais e erros somente por códigos/campos/índices. Nenhum valor é persistido. Configuração e plano em memória continuam sensíveis: serialização arbitrária não é sanitização e não deve alimentar logs, relatórios ou SSE. Esses canais ainda não existem; sua integração exige DTOs por allowlist e testes de canários.

Authorized exige TestAuthorizationDeclared e TestCredentialHeaderIndex válido no snapshot local. O marcador identifica a credencial declarada pelo chamador; não verifica emissão, propriedade ou validade da credencial. Não adiciona header de bypass, não relaxa loopback/quotas e não prova grant. Normal e Diagnostic também aceitam autenticação. O modo diagnóstico não realiza sondagens ou adaptação.

Exceção explícita de laboratório: HTTP com credenciais sintéticas em loopback permanece permitido, conforme comportamento já existente da FICHA-001. Usar somente segredos sintéticos nesses testes; o validador puro não distingue um segredo real de um sintético. Para o SaaS remoto, permanece obrigatório HTTPS, referência versionada, cofre, autorização e revogação de M3. Nenhum destino remoto foi liberado.

IPs de egress conhecidos podem ser informados para configuração da infraestrutura pelo próprio cliente, com origem/validade e ressalva de NAT compartilhado. Não garantem exclusividade, estabilidade ou dispensa de limitação. Não implementar proxy, rotação ou evasão.
