# Painel React local

O painel integra a engine M1 e o PostgreSQL M2, com editor de carga constante, execução real, SSE, cancelamento, histórico paginado, gráfico e exportação da visão pública. O layout funciona em desktop e celular. Dados vazios, indisponibilidade, resultado parcial e falha de gravação são apresentados explicitamente.

É uma antecipação **local** das fichas 029–032, registrada na [030](implementation-cards/FICHA-030-REACT-LOCAL.md). M6 completo depende de M3–M5: login, sessões, autorização por usuário, cofre, grants remotos e workers distribuídos ainda não existem. O perfil em degraus 033 também permanece pendente.

## Iniciar no Linux/WSL

Requisitos: Docker Compose, Node.js 22.12+ com npm e .NET SDK 10.0.204. Na raiz:

```sh
./scripts/run-web.sh
```

O script usa `dotnet` disponível no PATH ou o SDK desta sessão em `/tmp/dinamico-sdk/dotnet`. Este caminho temporário não substitui uma instalação permanente do SDK. O Compose usa `127.0.0.1:55432` para PostgreSQL e volume persistente; o painel usa **http://127.0.0.1:5080** e a API é servida na mesma origem.

Na ausência de configuração, o script usa a conexão de desenvolvimento do Compose. Para outra senha, porta ou banco, exporte `DINAMICO_DATABASE_CONNECTION`; senhas não entram em argumentos ou mensagens da aplicação. `DINAMICO_POSTGRES_PORT` altera a porta do Compose e a porta padrão usada pelo script. Com `DINAMICO_POSTGRES_PASSWORD` personalizado, informe também a conexão completa.

O SDK Windows via `dotnet.exe` não é selecionado automaticamente pelo script WSL. No Windows, execute manualmente os comandos abaixo em PowerShell, com `dotnet` do SDK Windows:

```powershell
docker compose -f compose.m2.yaml up -d --wait
npm ci --prefix FrontEnd
npm run build --prefix FrontEnd
dotnet restore Dinamico.slnx --locked-mode
dotnet build Dinamico.slnx -c Release --no-restore
$env:DINAMICO_DATABASE_CONNECTION = 'Host=127.0.0.1;Port=55432;Username=dinamico;Password=dinamico-local-m2;Database=dinamico_m2'
dotnet src/Dinamico.Web/bin/Release/net10.0/Dinamico.Web.dll
```

Inicie sua aplicação alvo separadamente. O painel não cria um alvo de teste de produção. Por exemplo, com sua aplicação em `http://127.0.0.1:5000/`, informe esse endpoint, escolha 10 req/s e clique em **Iniciar teste**. O motor aceita domínios HTTP/HTTPS, inclusive `https://app-hub.cplus.com.br/` e `localhost`. DNS é resolvido no transporte, respeitando o timeout de conexão; HTTPS mantém hostname/SNI e validação do certificado. Redirecionamentos não são seguidos. IPs literais continuam restritos ao loopback.

Ctrl+C interrompe a execução ativa e tenta salvar o resultado durante o encerramento limitado do servidor. Crash/SIGKILL não garantem relatório ou recuperação; o controle persistido de execução é trabalho de M3. O banco permanece ativo; para pará-lo, use `docker compose -f compose.m2.yaml stop`.

## Desenvolver o frontend

Terminal 1, na raiz, com o banco ativo e a conexão definida:

```sh
export DINAMICO_DATABASE_CONNECTION='Host=127.0.0.1;Port=55432;Username=dinamico;Password=dinamico-local-m2;Database=dinamico_m2'
dotnet run --project src/Dinamico.Web
```

Terminal 2:

```sh
npm ci --prefix FrontEnd
npm run dev --prefix FrontEnd
```

Abra **http://127.0.0.1:5173**. Vite encaminha `/api` para `127.0.0.1:5080`. Para servir o frontend compilado pela API, execute `npm run build --prefix FrontEnd` antes de iniciar a API. Após um novo build, recarregue o navegador. O backend encontra `FrontEnd/dist` a partir da raiz ou do diretório do projeto.

## Uso e interpretação

- **Projeto:** o ícone de configurações permite trocar o UUID que delimita o histórico. Só esse identificador é salvo no localStorage. Isso não autentica o operador nem implementa isolamento multiusuário.
- **Opções avançadas:** timeout, modos Normal/Diagnóstico/Autorizado, headers por linha e corpo UTF-8 de até 64 KiB. Headers e corpo não são salvos no browser. Use credenciais sintéticas no laboratório local. Authorized exige índice do header e declaração explícita, sem alterar os limites.
- **Ao vivo:** snapshots SSE a cada segundo; os contadores são agregados em janelas de 5 s. Reabrir a página recupera a execução ativa e reconecta à mesma execução, sem iniciar outra carga. Durante uma janela ainda aberta, os contadores podem permanecer inalterados.
- **Resultados:** P50/P95/P99 derivam dos buckets de respostas HTTP completas. Sem população ou com overflow não finito, a tela mostra `—`. “Sem sucesso” reúne HTTP completo fora de 2xx e outros terminais; “Não iniciadas” reúne os descartes do gerador, sem contar o trecho não agendado após parada. 429 usa status finais como denominador.
- **Histórico:** o backend persiste via importação canônica M2. Se a gravação falhar, o painel mantém a falha explícita e permite baixar a visão em memória. Após reinício, somente resultados realmente salvos permanecem. A retenção M2 pode remover as janelas; nesse caso o resumo continua disponível enquanto retido.
- **Download:** o JSON baixado contém a visão pública do painel, sem URL, headers ou payload; não é o relatório canônico usado pelo comando `storage import`. Continue usando a CLI para gerar relatórios de importação/exportação canônica.

A aplicação aceita uma execução ativa por processo e mantém cache de no máximo 20 execuções finalizadas, cada uma com até 133 janelas. Operar dois servidores locais separadamente não coordena essa quota; leases e quotas persistidas são M3. A API só escuta em loopback, confere host, rejeita origem externa em mutações e exige `X-Dinamico-Client: local-ui`. Não publique este adaptador local por túnel/proxy como serviço autenticado.

## Diagnóstico de requisições e recursos

O resultado mostra planejadas, agendadas, iniciadas, HTTP completas e 2xx. A tabela de causas separa atraso do scheduler, limite de concorrência, política, transporte, timeout, cancelamento, limite de resposta e falha interna. Percentuais indicam o denominador; status recebido não é outro desfecho terminal. A linha planejada do gráfico representa somente o intervalo de geração configurado, não o drain. Em cancelamento, o perfil original continua como referência; as não agendadas após parada aparecem separadamente.

`dataComplete` significa cobertura dos dados agregados, não cumprimento da taxa. Uma execução pode terminar com dados completos e `GeneratorLimited`. Os diagnósticos do scheduler detalham descartes por tolerância, teto por despertar e fronteira final. Esses valores explicam o despacho do gerador; recebimento e processamento no alvo exigem evidência independente.

O seletor do gráfico oferece taxa, latências, descartes/falhas, memória, CPU, fila HTTP e threads/conexões, com tabela acessível por janela. P50/P95/P99 e mínimo/máximo HTTP usam apenas respostas completas. O P95 terminal inclui falhas; um timeout não é uma resposta HTTP completa. Recursos são do **processo gerador**, amostrados ao consumir cada janela, com instante real; não representam memória ou CPU do alvo. CPU usa deltas e % de um núcleo, podendo superar 100%. Alocações/GC são cumulativos. Conexões/fila HTTP abrangem todo o processo; conexões são um saldo relativo e podem ser negativas. Dados indisponíveis aparecem como ausência.

Nas opções avançadas, **Captura de requisições** permite:

- **Desligada:** mantém contadores, histogramas e recursos, sem detalhe individual.
- **Amostrada:** retém até 1.000 operações: primeiras 250, primeiras 250 com falha ou status >=400 e as 500 mais lentas, deduplicadas. É seleção diagnóstica, sem representatividade estatística garantida.
- **Completa:** exige até 2.000 requisições planejadas. Registra todos os terminais observados; só confirma cobertura completa da captura após drain e sem descarte da captura. Não inclui intenções que nunca foram iniciadas, explicadas pelos contadores.

A tabela individual permite ordenar por início, duração ou falhas e mostra duração terminal, tempo até headers quando medido, status e categoria. Tempo até headers inclui espera do cliente e rede; não isola tempo de servidor. Cobertura individual é separada da cobertura agregada. A captura não guarda URL, headers, payload ou mensagem de exception.

Recursos/scheduler e metadados da captura são salvos no PostgreSQL com retenção de resumo de 30 dias; detalhes individuais e janelas são removidos aos sete dias. O painel executa a retenção canônica ao iniciar e a cada minuto, em lotes de até 200 execuções, com timeout de dez segundos e nova tentativa na próxima rodada se houver falha. Resumos expiram aos 30 dias e identidades aos 90 dias. Com o painel desligado, a limpeza aguarda a próxima inicialização ou o comando `storage prune`. A retenção do backend externo de traces é configurada separadamente. O máximo de espera HTTP é cumulativo desde o início do coletor, não uma medida isolada da janela. A migração 2 é aditiva e validada por checksum; execuções antigas continuam legíveis sem diagnósticos inventados. CLI também aceita `captureMode` (`Off`, `Sampled`, `Complete`) e `correlateRequests` no JSON de configuração e inclui diagnósticos no relatório/importação canônica.

### Correlação e exportação opcional

Há um [laboratório local pronto com Jaeger e alvo instrumentado](TELEMETRY-LOCAL.md), separado da sua aplicação. O script `scripts/validate-telemetry.mjs` verifica a cadeia completa no backend real e reconcilia um caso de pressão de concorrência com o contador independente do alvo. Para investigar a sua execução real, use os parâmetros e a instrumentação da sua aplicação.

Ative **Correlacionar requests com telemetria** com captura habilitada para propagar `traceparent` W3C. Sem opt-in, o transporte desliga a propagação automática de atividades do host. Headers explícitos `traceparent`/`tracestate` conflitam com correlação gerenciada e são recusados nesse modo. Complete correlaciona até 2.000 requests; Sampled seleciona deterministicamente no máximo 1.000 intenções distribuídas pelo plano. Nem toda amostra retida terá trace.

Configure no **host**, antes de iniciar a API ou CLI:

```powershell
$env:DINAMICO_OTLP_TRACES_ENDPOINT = 'http://127.0.0.1:4318/v1/traces'
$env:DINAMICO_TRACE_URL_TEMPLATE = 'http://127.0.0.1:16686/trace/{traceId}'
# Se o coletor exigir autenticação, configure DINAMICO_OTLP_BEARER_TOKEN no ambiente do host.
```

Em Bash, use `export NOME='valor'`. Com `dotnet.exe` a partir do WSL, inclua esses nomes em `WSLENV`. O endpoint exige URL HTTP(S) sem userinfo, query ou fragmento; o template exige exatamente um `{traceId}` no path, também sem userinfo/query/fragmento. Credenciais do exportador não entram na API, banco, download ou SSE.

A integração segue [OTLP/HTTP com JSON](https://opentelemetry.io/docs/specs/otlp/): exporta somente spans das operações retidas que têm IDs, em lotes de até 128, depois de terminar a carga, com prazo total de dois segundos e sem retries. Não cria um trace gigante por execução. O estado de exportação (`disabled`, `not-configured`, `exported`, `failed`) é separado da completude dos dados do teste. Backend indisponível não muda a carga nem os contadores. O envio pode acrescentar até dois segundos antes da publicação final; o tempo do teste continua sendo o medido pelo motor.

Instrumente o alvo e suas dependências com OpenTelemetry e envie seus spans ao backend escolhido. O link abre o trace; SQL, locks, filas internas e memória do alvo dependem dessa instrumentação. O Dinamico não deduz esses gargalos pelo P95. Não há backend de telemetria embutido nem instalação automática de coletor.

## Verificar

```sh
npm ci --prefix FrontEnd
npm run build --prefix FrontEnd
dotnet restore Dinamico.slnx --locked-mode
dotnet build Dinamico.slnx -c Release --no-restore
```

Testes .NET exigem `DINAMICO_TEST_DATABASE` apontando para **um banco exclusivo**, previamente criado. Os testes PostgreSQL instalam triggers de falha e alteram temporariamente metadados de migrations; não use dados úteis. Exemplo Bash:

```sh
export DINAMICO_TEST_DATABASE='Host=127.0.0.1;Port=55432;Username=dinamico;Password=dinamico-local-m2;Database=dinamico_m2_web_test'
dotnet test Dinamico.slnx -c Release --no-build --no-restore -m:1 --logger trx --results-directory TestResults/Web/Suite
```

Com a API servindo o build React em 5080 e o banco de desenvolvimento ativo:

```sh
cd FrontEnd
npx playwright install chromium
npm run test:e2e
```

O teste de navegador cria seu próprio alvo HTTP em loopback e usa UUIDs de projetos novos; deixa somente resultados sintéticos no banco configurado da API. Verifica execução/SSE/persistência/histórico/download, cancelamento, rejeição de URL com fragmento, layout móvel, ajuda, troca de projeto e estado de banco indisponível. Screenshots ficam em `TestResults/Web`. O relatório de teste fica em `FrontEnd/playwright-report` e traces em `FrontEnd/test-results`, todos ignorados no Git.

Validação de 2026-10-22: **585 testes .NET aprovados** (575 motor/CLI + 7 persistência + 3 API), build Release sem avisos/erros e **3 testes de navegador aprovados** com Chromium real. Build React/TypeScript e script de inicialização completo aprovados; desktop 1440 px e mobile 390 px sem overflow horizontal. O navegador também confirmou que recarregar durante o teste recupera o mesmo identificador de execução. Um ensaio posterior encontrou uma corrida no cliente do teste de corpo grande: foi corrigido para enviar JSON válido com `Expect: 100-continue` e verificar a rejeição pelo limite antes do envio, em vez de disputar o upload com o fechamento da conexão. A API foi retestada após a correção. Windows e browsers diferentes não foram revalidados nesta entrega.
