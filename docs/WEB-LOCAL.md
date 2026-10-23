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

Inicie sua aplicação alvo separadamente. O painel não cria um alvo de teste de produção. Por exemplo, com sua aplicação em `http://127.0.0.1:5000/`, informe esse endpoint, escolha 10 req/s e clique em **Iniciar teste**. Endereços DNS, `localhost` como alvo e destinos externos são recusados pelo motor. O navegador pode acessar o painel por localhost, mas o alvo de carga exige IP literal.

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

O teste de navegador cria seu próprio alvo HTTP em loopback e usa UUIDs de projetos novos; deixa somente resultados sintéticos no banco configurado da API. Verifica execução/SSE/persistência/histórico/download, cancelamento, rejeição de alvo externo, layout móvel, ajuda, troca de projeto e estado de banco indisponível. Screenshots ficam em `TestResults/Web`. O relatório de teste fica em `FrontEnd/playwright-report` e traces em `FrontEnd/test-results`, todos ignorados no Git.

Validação de 2026-10-22: **585 testes .NET aprovados** (575 motor/CLI + 7 persistência + 3 API), build Release sem avisos/erros e **3 testes de navegador aprovados** com Chromium real. Build React/TypeScript e script de inicialização completo aprovados; desktop 1440 px e mobile 390 px sem overflow horizontal. O navegador também confirmou que recarregar durante o teste recupera o mesmo identificador de execução. Um ensaio posterior encontrou uma corrida no cliente do teste de corpo grande: foi corrigido para enviar JSON válido com `Expect: 100-continue` e verificar a rejeição pelo limite antes do envio, em vez de disputar o upload com o fechamento da conexão. A API foi retestada após a correção. Windows e browsers diferentes não foram revalidados nesta entrega.
