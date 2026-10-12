# 01 — Visão do produto

## Problema e público

Desenvolvedores plenos precisam descobrir como uma API se comporta sob uma taxa de chegada definida, sem aprender uma linguagem de scripting ou operar manualmente vários geradores. Hoje, uma configuração aparentemente simples pode esconder limitações de concorrência, percentis calculados incorretamente e saturação do próprio computador gerador.

O SaaS oferecerá configuração visual, execução reproduzível, distribuição de tráfego e explicação dos resultados. Sua proposta de valor depende tanto de **mostrar quando a medição é insuficiente** quanto de gerar carga. Não promete encontrar automaticamente a capacidade máxima de qualquer aplicação.

## Requisitos confirmados

Stack: ASP.NET Core e motor próprio em .NET 10, React/TypeScript, PostgreSQL, Kubernetes Jobs, Helm e Minikube. Control Plane em monólito modular e worker em processo independente. Interface REST + SSE. Logs JSON e métricas operacionais, armazenamento agregado e consumo limitado de recursos. Não usar motores prontos, Redis, brokers, OLAP, service mesh, Operators ou CRDs próprios no início.

## Casos de uso

1. Um desenvolvedor registra um destino autorizado, cria um projeto e descreve uma chamada HTTP com método, headers, credencial referenciada e payload estático.
2. Escolhe taxa e duração; a plataforma valida autorização, orçamento de requisições e recursos antes de aceitar a execução.
3. Acompanha carga solicitada, iniciada, concluída, falhas e percentis. Recebe indicação de atraso de métricas, saturação e perda de workers.
4. Interrompe o teste; a interface distingue pedido de parada de parada confirmada.
5. Consulta um histórico reproduzível, com configuração versionada, versões do motor, limites e qualidade dos dados, sem recuperar segredos do resultado.

## Escopo proposto do MVP SaaS

O primeiro marco local é menor que o MVP. O MVP inclui login por convite, workspaces, projetos, revisões de testes, autorização de destinos públicos, execução com um ou vários workers, perfil constante e perfil em degraus, cancelamento, resultados agregados, amostras de erros e histórico com retenção.

Um projeto pode ter muitos testes; **cada teste executa um único endpoint**. Isso atende ao caso principal e impede que roteamento ponderado, jornadas e multiplicação de séries entrem no primeiro motor. Métodos permitidos: GET, HEAD, POST, PUT, PATCH, DELETE e OPTIONS. Credenciais inicialmente: Bearer, Basic sobre HTTPS e header de API key. Não haverá descoberta automática nem renovação OAuth no worker.

O primeiro marco implementável terá apenas CLI local, perfil constante, destino loopback explicitamente permitido, transporte HTTP/1.1 e relatório JSON. O SaaS posteriormente admitirá HTTP/HTTPS para destinos públicos aprovados, com HTTPS obrigatório para credenciais. HTTP/2 será uma extensão após medir separadamente streams, conexões e filas do handler.

## Requisitos funcionais

| ID | Comportamento verificável |
|---|---|
| RF01 | Autenticar usuário e autorizar cada recurso por workspace/projeto |
| RF02 | Cadastrar teste e preservar revisões imutáveis referenciadas por execuções |
| RF03 | Verificar destino, credenciais, quotas e configuração antes de gerar tráfego |
| RF04 | Aceitar início idempotente e expor seu estado persistido |
| RF05 | Distribuir um plano fixo entre workers, com início coordenado e sem reposição durante carga |
| RF06 | Exibir métricas, cobertura, atualização mais recente e falhas por categoria |
| RF07 | Cancelar idempotentemente, informar confirmação ou incerteza de parada |
| RF08 | Consultar histórico paginado e excluir dados conforme retenção |
| RF09 | Registrar auditoria de alterações, execução, parada e uso de credenciais, sem seus valores |
| RF10 | Recuperar coordenação após reinício sem repetir efeitos conhecidos ou reexecutar carga |

## Requisitos não funcionais

| ID | Requisito e validação |
|---|---|
| RNF01 | Memória limitada por concorrência, buffers e janelas, não pelo total de requisições; medir plateau em execução prolongada |
| RNF02 | Percentis derivados de histogramas compatíveis; testar contra conjunto ordenado conhecido e limite de erro documentado |
| RNF03 | Queda do gerador não aparece como queda de latência do alvo; publicar drops, atraso e qualidade |
| RNF04 | Isolamento entre workspaces e bloqueio SSRF; testes negativos são gate para execução remota |
| RNF05 | Operações assíncronas recuperáveis e métricas idempotentes; injetar falhas em cada fronteira de commit |
| RNF06 | Atualização da UI baseada em janela de 5 s; meta experimental de disponibilidade em até 10 s após fechamento, aferida sob carga |
| RNF07 | Não persistir eventos individuais de requisição; armazenamento cresce com duração, workers e séries limitadas |
| RNF08 | Build e testes reproduzíveis, configuração validada e imagens com versão/digest |
| RNF09 | Parada cooperativa rápida; na perda de controle, parar novos envios ao expirar lease de 10 s; medir atraso real do escalonador |

Não há promessa de 50.000 RPS, disponibilidade comercial ou economia percentual. Os volumes de 1.000, 15.000 e 50.000 RPS são cenários de dimensionamento, não capacidades demonstradas.

## Fora do escopo e evolução

Fora do MVP: scripts, jornadas com estado, navegador real, WebSocket/gRPC, arquivos de payload grandes, dados dinâmicos por request, autoescalabilidade durante execução, múltiplas regiões, comparação estatística automática, cobrança, SSO corporativo e agentes dentro da rede do cliente. Redes privadas não serão abertas mediante simples checkbox.

Evoluções possíveis, mediante evidência: HTTP/2, múltiplos endpoints com cardinalidade controlada, rampas lineares, agentes privados com modelo próprio de confiança, exportação de resultados, integração CI, agregações de retenção longa e armazenamento analítico se PostgreSQL se tornar o gargalo medido.

## Hipóteses e decisões de produto pendentes

- Proposto: acesso inicial por convite e aprovação administrativa de destinos, além de prova de controle do hostname.
- Proposto: retenções de 7 dias para detalhe, 30 dias para resumo e 90 dias para auditoria, definidas em 04.
- Pendente: quotas comerciais e concorrência de execuções; os tetos técnicos serão medidos antes de liberar o serviço.
- Pendente: se um endpoint por teste atende aos primeiros usuários. A extensão exige rever contrato e armazenamento, sem impedir o marco local.
- Pendente: aceitação de erro de quantização de aproximadamente 2,19% nos percentis. A proposta oferece contagem exata e quantil aproximado explícito.

## Identificação de Rate Limiting (FICHA-037)

Normal usa as políticas existentes; Autorizado usa credenciais específicas de teste sem presumir retirada de limites; Diagnóstico destaca evidências sem sondagem adicional ou adaptação da carga. Todos os modos identificam 429. A regra de aceitação HTTP é explícita e versionada, sem comprovar processamento de negócio. Não oferecer bypass por header, rotação de IP ou proxies de evasão.

Em 2026-10-12, o recorte implementado é a extensão dos contratos locais da FICHA-001. Relatórios, cofre, egress e interface dependem dos marcos da [FICHA-037](implementation-cards/FICHA-037-RATE-LIMITING.md); o SaaS distribuído ainda não está implementado.
