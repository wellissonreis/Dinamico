# Dinamico — Load Engine

Estado atual (2026-10-12): contratos/validação v2 e parser Retry-After implementados (001/037). A continuação adiciona a fixture HTTP loopback da FICHA-002 e seus testes. FICHA-037 permanece **M1 parcial**: transporte do motor, scheduler, agregação, relatório, Control Plane e UI pendentes. Evidências e limites em [RATE-LIMITING-DELIVERY](docs/RATE-LIMITING-DELIVERY.md).

O [contrato local v2](docs/03-LOAD-ENGINE-DESIGN.md#rate-limiting--contrato-local-v2-2026-10-12) fixa a integração. Evidências do recorte estão em [RATE-LIMITING-DELIVERY](docs/RATE-LIMITING-DELIVERY.md). Validação atual: **365 testes aprovados**, zero falhas/ignorados; build Release sem avisos/erros. Os 119 testes de 2026-10-09 permanecem evidência histórica da base.

## Compilar e testar

Requer SDK **10.0.204**, fixado em [global.json](global.json), sem seleção de versões preview. Todos os projetos usam `net10.0`; o host é framework-dependent e usa patches compatíveis do runtime .NET 10 instalado. Na entrega histórica da FICHA-001 (2026-10-09), a validação ocorreu no Windows via WSL, com runtime 10.0.12.

```sh
dotnet restore Dinamico.slnx --locked-mode
dotnet build Dinamico.slnx --configuration Release --no-restore
dotnet test Dinamico.slnx --configuration Release --no-build --no-restore --logger trx --results-directory TestResults
```

No WSL deste ambiente, use `dotnet.exe` em vez de `dotnet` para acessar o SDK Windows. Os comandos podem ser executados diretamente no Windows usando `dotnet`. Resultados TRX ficam em `TestResults/`, excluído do versionamento, assim como `bin/` e `obj/`.

## Comportamento disponível

`LocalPlanValidator.Validate(configuration, executionId)` devolve um `PlanValidationResult`: plano validado ou coleção de erros. A função não faz DNS, HTTP, acesso a arquivos, logging ou leitura do relógio. O chamador fornece o identificador da execução, evitando efeitos implícitos na validação.

Exemplo de consumo dos contratos, sem executar carga:

```csharp
var configuration = new LoadTestConfiguration
{
    Url = "http://127.0.0.1:5000/products",
    RequestsPerSecond = 1_000,
    DurationSeconds = 60,
    MaxConcurrency = 256
};

var result = LocalPlanValidator.Validate(configuration, Guid.NewGuid());
// result.Plan.PlannedTotal será 60.000 se a entrada for válida.
// Não enviar nem registrar configuração/plano completos: podem conter segredos.
```

As classes ficam nos namespaces `Dinamico.LoadEngine.Contracts` e `Dinamico.LoadEngine.Validation`.

## Configuração local v2 — recorte autorizado

Exemplo dos contratos v2 implementados, sem enviar carga:

```csharp
var configuration = new LoadTestConfiguration
{
    Url = "http://127.0.0.1:5000/products",
    RateLimitMode = RateLimitMode.Authorized,
    RetryPolicy = LoadRetryPolicy.None,
    SuccessRule = HttpSuccessRule.Completed2xxV1,
    Headers = [new HttpHeader("Authorization", "Bearer synthetic")],
    TestCredentialHeaderIndex = 0,
    TestAuthorizationDeclared = true
};
var result = LocalPlanValidator.Validate(configuration, Guid.NewGuid());
// Plano válido: SchemaVersion = 2. Não executa HTTP.
```

`Normal` (default) e `Diagnostic` também aceitam credenciais em `Headers`; Diagnostic mantém o perfil e não faz sondagens. Authorized exige índice válido de header com valor não vazio e declaração local verdadeira. Essa declaração não é grant, prova de autorização ou dispensa de limites; não verifica emissão, propriedade ou validade da credencial. Os limites, colisões sem distinção de caixa e proibições de headers continuam aplicáveis; valores ficam em memória, com cofre/referências versionadas somente em M3.

O plano usa `SchemaVersion = 2`; consumidores futuros rejeitam versões desconhecidas. Somente `LoadRetryPolicy.None` e `HttpSuccessRule.Completed2xxV1` são aceitos. Sucesso exige corpo completo e status 200–299, inclusive 202, sem comprovar processamento de negócio. `IsRateLimited` depende somente de 429, mesmo após falha do corpo; 503 com Retry-After não ativa o sinal.

O parser puro recebe status, valores de Retry-After e referência UTC do chamador. Só avalia 429; a observação distingue NotObserved (default, captura não realizada), NotApplicable (status diferente de 429), Missing, Invalid, DeltaSeconds e HttpDate, sem reter texto bruto. Um 429 sem captura explícita permanece NotObserved; Missing exige captura efetiva sem campo. Delay é nulo nas quatro primeiras categorias. NotObserved não comprova ausência do header; NotApplicable em 429 ou evidência aplicável em outro status é erro de contrato. Limita o campo a 128 caracteres antes do trim e a duração a 86.400 s, com clamp explícito. Datas passadas viram zero e datas HTTP sinalizam incerteza do relógio. Não lê relógio, faz I/O, espera, altera agenda/RPS/concorrência ou cria retry. Acumulador e relatório dessas observações continuam futuros.

Conforme [07 — segurança](docs/07-SECURITY.md), HTTP com credenciais sintéticas em loopback é permitido no laboratório. Usar somente segredos sintéticos nesses testes; o validador puro não distingue segredos reais de sintéticos. O SaaS remoto exige HTTPS e os controles de M3.

## Decisões históricas da FICHA-001 (2026-10-09)

- **Plano imutável:** propriedades somente de leitura e cópias defensivas em `ImmutableArray`, disponível no .NET. Headers/payload são copiados uma vez por plano, sem guardar uma lista de chegadas ou tarefas.
- **Destino local explícito:** somente HTTP(S) com IP loopback literal. Aceita IPv4 loopback, `::1` e IPv4 loopback mapeado em IPv6; recusa `localhost`, nomes DNS, IPv4 abreviado/numérico, scopes IPv6, userinfo e fragmentos. A ficha permite literal ou resolução pinada; escolhemos literal para manter validação sem I/O. Conectar ao IP validado será responsabilidade da FICHA-003.
- **Limites:** RPS 1–1.000; duração inteira 1–600 s; concorrência 1–256 (default 64); timeout positivo até 60 s (default 10 s); payload até 64 KiB; resposta 1 byte–1 MiB. Defaults de entrada: 100 RPS e 30 s, conforme perfil conservador documentado. O total usa `long` com multiplicação `checked`.
- **Detalhes operacionais explicitados:** URL até 8.192 caracteres; até 128 headers; bloco de headers configurados até 16 KiB, contando `: ` e CRLF; somente nomes token e valores ASCII imprimíveis/HTAB. Duplicatas sem distinção de caixa são recusadas; múltiplos valores devem caber em um valor válido. Headers de destino, framing, conexão, proxy e `Expect` ficam sob controle do transporte. O teto de headers de resposta é 16 KiB. Esses limites tornam custo e volume de validação finitos; não mudam a arquitetura. A FICHA-003 também terá de respeitar o teto total na mensagem efetiva, incluindo headers automáticos.
- **Métodos:** GET, HEAD, POST, PUT, PATCH, DELETE e OPTIONS, com grafia exata. Não normalizar silenciosamente uma entrada inválida.
- **Erro seguro:** somente enum de código, enum de campo e índice opcional do header. Cada erro identifica `configuration.rejected`; o host futuro emitirá esse evento. Não adicionamos infraestrutura de logs antes da FICHA-010. `ToString()` de configuração/header/plano não imprime valores sensíveis; isso não torna segura a serialização arbitrária dos objetos completos.
- **Observação terminal:** valor transitório com índice de chegada, duração, status opcional, bytes e desfecho exclusivo. HTTP 500 completo pertence à população HTTP completa; um 200 seguido de timeout do corpo não pertence. Não há coleta nem agregação implementadas. `default(RequestObservation)` é `Unknown`, nunca uma conclusão válida.
- **Relógio:** somente o contrato `IMonotonicClock`; captura real e agenda pertencem às fichas 004/005.

Nenhum pacote NuGet foi adicionado ao motor ou ao host. Os testes usam xUnit 2.9.3 e os adaptadores `Microsoft.NET.Test.Sdk` 17.14.1 / `xunit.runner.visualstudio` 3.1.5 para `dotnet test`; versões transitivas estão no lockfile. Esse é o arranjo de execução descrito pela [documentação do xUnit](https://xunit.net/docs/getting-started/v2/getting-started). Não há biblioteca de mocks, validação ou geração de carga.

## Arquivos da implementação histórica (2026-10-09)

| Arquivo | Finalidade |
|---|---|
| [global.json](global.json), [Directory.Build.props](Directory.Build.props), [Dinamico.slnx](Dinamico.slnx), [.gitignore](.gitignore) | SDK, .NET 10, solução e exclusão de artefatos |
| [Dinamico.LoadEngine.csproj](src/Dinamico.LoadEngine/Dinamico.LoadEngine.csproj) | Biblioteca sem pacotes externos |
| [LocalEngineLimits.cs](src/Dinamico.LoadEngine/Contracts/LocalEngineLimits.cs) | Tetos e defaults compartilhados |
| [LoadTestConfiguration.cs](src/Dinamico.LoadEngine/Contracts/LoadTestConfiguration.cs) | Entrada não confiável e header |
| [LoadTestPlan.cs](src/Dinamico.LoadEngine/Contracts/LoadTestPlan.cs) | Snapshot validado e versionado |
| [ConfigurationError.cs](src/Dinamico.LoadEngine/Contracts/ConfigurationError.cs) | Erros seguros e resultado da validação |
| [RequestObservation.cs](src/Dinamico.LoadEngine/Contracts/RequestObservation.cs) | Observação terminal e enums de qualidade/erro |
| [IMonotonicClock.cs](src/Dinamico.LoadEngine/Contracts/IMonotonicClock.cs) | Fronteira do relógio testável |
| [LocalPlanValidator.cs](src/Dinamico.LoadEngine/Validation/LocalPlanValidator.cs) | Validação pura e criação do plano |
| [Host local](src/Dinamico.LoadEngine.Local/Dinamico.LoadEngine.Local.csproj), [Program.cs](src/Dinamico.LoadEngine.Local/Program.cs) | Estrutura mínima do executável, sem engine runner |
| [Projeto de testes](tests/Dinamico.LoadEngine.Tests/Dinamico.LoadEngine.Tests.csproj), [packages.lock.json](tests/Dinamico.LoadEngine.Tests/packages.lock.json) | Dependências de teste reproduzíveis |
| [LocalPlanValidatorTests.cs](tests/Dinamico.LoadEngine.Tests/LocalPlanValidatorTests.cs) | Defaults, destinos, limites, overflow e sigilo |
| [HeaderAndSnapshotTests.cs](tests/Dinamico.LoadEngine.Tests/HeaderAndSnapshotTests.cs) | Headers, orçamento e isolamento do snapshot |
| [RequestObservationTests.cs](tests/Dinamico.LoadEngine.Tests/RequestObservationTests.cs) | Desfechos e separação das populações |

O SDK gerou `packages.lock.json`; nenhum arquivo de aplicação foi gerado por template. Não foi inicializado um repositório Git: o diretório reservado `.git` existente não é reconhecido como repositório neste ambiente.

## Documentação e próxima etapa

Consulte a [arquitetura](docs/README.md), o [guia didático](docs/LOAD-ENGINE-EXPLAINED.md) e a [FICHA-001 com evidências de aceite](docs/implementation-cards/FICHA-001-CONTRACTS.md). A autorização atual cobre somente a extensão de contratos/validação e parser puro sobre a ficha 001. Os demais componentes continuam futuros; os registros anteriores são históricos.

Próxima integração: [FICHA-003 — transporte e streaming](docs/implementation-cards/FICHA-003-HTTP-TRANSPORT.md), usando a fixture controlada da 002.
