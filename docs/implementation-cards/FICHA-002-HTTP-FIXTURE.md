# FICHA-002 — Fixture HTTP controlada

Prioridade: P0. Complexidade: P. Marco: M1. Status: implementada e verificada no laboratório local (2026-10-12).

## Objetivo

Disponibilizar um alvo local previsível para provar comportamentos do motor, com contagem independente das requisições recebidas.

## Contexto arquitetural

É infraestrutura de teste, separada do motor. Futuramente terá modo de laboratório no Minikube; nesta ficha roda só em loopback.

## Conceitos necessários

Teste de integração usa rede real e pode revelar buffering/timeout que um mock omite. A contagem no servidor é independente de conclusão no cliente: timeout não prova que o alvo deixou de receber a chamada.

## Responsabilidades

Expor respostas pequenas, status controlado, atraso antes de headers, corpo lento, corpo grande, truncamento e redirect local. Contar recebimentos/conexões de forma limitada, sem log por request.

## Fora do escopo

Simulador universal, benchmark comercial, Kubernetes, TLS inseguro ou endpoint público.

## Dependências

FICHA-001.

## Contratos

Setup de teste recebe comportamento e limites; devolve endereço loopback/porta efêmera e acesso ao contador. Encerramento cancela handlers, drena recursos e libera porta. Nenhuma URL de terceiro é recebida para redirect.

## Integração futura — Rate Limiting v2

A fixture desta ficha foi implementada na continuação da 037. A integração com o transporte do motor continua pendente na 003; o recorte anterior entregou contratos/parser da 001/037. Usar o [contrato local v2](../03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12); evidências do recorte estão em [RATE-LIMITING-DELIVERY](../RATE-LIMITING-DELIVERY.md).

Adicionar cenários loopback: 429 contínuo/intermitente e políticas por credencial sintética; Retry-After comprovadamente ausente após captura (Missing), zero, segundos, datas HTTP passadas/futuras, inválido, duplicado e extremo. Se Kestrel não reproduzir campo malformado, usar transporte controlado limitado. Incluir 503 com Retry-After para provar que não ativa IsRateLimited; 200/202 completos e 2xx incompleto para a regra Completed2xxV1; 429 com corpo lento/truncado para preservar status após falha.

Contagem independente deve verificar a ausência de retry adicionado pelo motor nos três modos, registrando eventuais recuperações internas do transporte .NET conforme 03; credenciais sintéticas funcionam também em Normal/Diagnostic e Authorized pode receber 429. HTTP loopback com essas credenciais é permitido conforme [07](../07-SECURITY.md); o validador não verifica sua origem nem distingue segredos reais de sintéticos. Sem captura, a observação fica NotObserved e não comprova ausência de Retry-After. Não guardar valores de credenciais nem lista de requests; verificar canários no resumo da fixture. NAT/egress e origem comum entre workers são ensaios somente de M4/M5, não requisito de entrega desta fixture M1. Os testes da fixture usam HttpClient simples; não comprovam ainda retries, agenda ou métricas do motor.

## Decisões técnicas

ASP.NET Core/Kestrel na stack .NET existente para servidor real. Usar transporte controlado adicional só para falhas que Kestrel não reproduzir; não manter arrays de milhões de recebimentos.

## Passos de implementação

1. Criar host de teste com bind exclusivo loopback e porta efêmera.
2. Implementar comportamentos selecionados por configuração de fixture.
3. Adicionar contadores atômicos e uma nova fixture por cenário, sem reset concorrente.
4. Garantir cleanup inclusive em assertion falha.
5. Registrar diferenças esperadas entre recebido e concluído.

## Testes obrigatórios

Confirmar status, tamanho, delays e truncamento com cliente simples; iniciar duas fixtures sem conflito; provar shutdown sem processos/portas presos. Fixture não aceita bind externo por default.

## Observabilidade

Resumo de inicialização/finalização e contagem recebida; zero conteúdo de requests. Modo diagnóstico opcional com limite de eventos.

## Critérios de aceite

Os cenários de falha são reproduzíveis e o teste obtém contagem independente. Nenhum acesso externo é necessário; encerramento funciona mesmo com corpo lento.

## Riscos e trade-offs

Fixture e motor no mesmo host competem por recursos. Ela valida correção, não prova capacidade do alvo/worker em máquinas separadas.

## Definição de pronto

- [x] Cenários e ownership do host testados.
- [x] Contadores limitados e porta liberada ao terminar.
- [x] Limitação do mesmo host descrita no relatório de testes.


## Evidência da continuação

Implementação: `tests/Dinamico.LoadEngine.Tests/Fixtures/ControlledHttpFixture.cs`; testes: `HttpFixtureTests.cs`. Kestrel HTTP/1.1, bind fixo em 127.0.0.1, porta efêmera, configuração isolada, sem providers de logging. Apenas FrameworkReference Microsoft.AspNetCore.App; nenhum pacote novo.

Contadores atômicos de recebimentos/conexões e handlers ativos; concorrência configurável com teto 1024 e rejeição imediata 503. Corpo até 16 MiB, chunk até 64 KiB, delays até 30 s, headers finitos, conexões/buffers limitados. Sobrecarga da fixture é 503, nunca evidência de rate limiting 429. Contadores são consultados após sincronização; o resumo não é um snapshot transacional para agregação.

Cenários: 200/202, 429 contínuo/intermitente, política por credencial sintética, 503, Retry-After ausente/zero/segundos/datas/inválido/duplicado/extremo, atraso de headers, corpo lento/grande/truncado e redirect relativo fixo. `ReleaseTruncation()` deve ser chamado após observar headers; o handler espera no máximo 10 s antes de abortar. A sincronização evita perda de headers causada por TCP reset imediato no Windows. Dispose cancela a espera e os delays.

Validação: restore locked, build Release normal sem avisos/erros, 365 testes aprovados (56 novos), zero falhas/ignorados. TRX: `TestResults/FixtureContinuation/fixture-continuation.trx`. Testes verificam cancelamento, contadores, snapshot dos headers, limites, canários no resumo/erros/ToString, fixtures simultâneas e porta liberada por conexão recusada seguida de rebind. SDK 10.0.204, runtimes .NET/ASP.NET 10.0.12, Windows x64 via WSL.

Fixture e cliente compartilham host: evidência funcional, não benchmark de capacidade. Credencial é exclusivamente sintética, retida apenas na configuração em memória; não existe histórico de requests. Não há teste de NAT, worker distribuído, cofre, SSE ou relatório. Próxima dependência: FICHA-003.
