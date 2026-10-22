# Persistência local — M2

Entrega validada em 2026-10-22: restore locked-mode, build Release sem avisos/erros, **575 testes do motor/CLI e 7 PostgreSQL** aprovados no Linux .NET 10.0.8, PostgreSQL 16.10. Nove testes direcionados também passaram em checkout isolado dos commits, sem as alterações M1 preexistentes da árvore de trabalho. Fluxo CLI com 12 verificações e persistência/replay após reinício real do banco aprovados. TRX e relatórios em `TestResults/M2/Final`, `Committed`, `Cli` e `Storage`. M2 não foi revalidado no runtime Windows nesta entrega.

M2 adiciona PostgreSQL à CLI. O motor continua independente do banco: primeiro produz o relatório, depois o operador o importa. Não há envio por request, execução remota, API HTTP ou autenticação nesta etapa. O UUID de projeto é um escopo lógico informado pelo operador local; não comprova autorização.

## Executar

SDK 10.0.204, Docker e PostgreSQL 16. Os exemplos abaixo são Bash e usam uma senha exclusivamente local de desenvolvimento. No PowerShell, atribua a conexão com `$env:DINAMICO_DATABASE_CONNECTION = '...'` e execute o mesmo binário com `dotnet`.

```sh
docker compose -f compose.m2.yaml up -d --wait
export DINAMICO_DATABASE_CONNECTION='Host=127.0.0.1;Port=55432;Username=dinamico;Password=dinamico-local-m2;Database=dinamico_m2'
dotnet restore Dinamico.slnx --locked-mode
dotnet build Dinamico.slnx -c Release --no-restore
dinamico() { dotnet src/Dinamico.LoadEngine.Local/bin/Release/net10.0/Dinamico.LoadEngine.Local.dll "$@"; }
dinamico storage migrate
```

O Compose publica somente em loopback e usa volume persistente. `DINAMICO_POSTGRES_PORT` altera a porta publicada; atualize a conexão também. `DINAMICO_POSTGRES_PASSWORD` configura a senha na criação do volume; mudar a variável não troca a senha de um banco já inicializado. A conexão não entra em argumentos da CLI nem é reproduzida nos erros.

Gere um relatório usando `run --config ... --output ...`, conforme o README. Em seguida escolha um UUID de projeto estável e importe:

```sh
dinamico storage import --project 11111111-1111-4111-8111-111111111111 --report report.json
dinamico storage list --project 11111111-1111-4111-8111-111111111111 --limit 20
dinamico storage show --project 11111111-1111-4111-8111-111111111111 --execution UUID_DO_RELATORIO
dinamico storage windows --project 11111111-1111-4111-8111-111111111111 --execution UUID_DO_RELATORIO --from 0 --to 10 --limit 11
dinamico storage measure
dinamico storage prune --batch 50
```

`list` retorna `nextCursor` opaco; passe-o como `--cursor TOKEN` para a próxima página do mesmo projeto. A ordenação é por instante de importação e UUID, descendentes. `windows` usa índices inclusivos; detalhe removido por retenção retorna lista vazia, com `detailRetained=false` no detalhe da execução. O limite é 100 execuções por página e 133 janelas por consulta. A CLI aplica deadline de 60 s às operações do banco.

| Código | Resultado |
|---|---|
| 0 | Operação concluída, incluindo replay idêntico |
| 2 | Entrada, relatório ou conexão ausente/inválida |
| 3 | Falha operacional de banco/saída ou timeout |
| 4 | Identidade existente com conteúdo diferente |
| 5 | Execução não encontrada no projeto consultado |

## Integridade e representação

Migrations são explícitas, transacionais e protegidas por advisory lock; checksum alterado ou versão desconhecida são rejeitados. O snapshot público v1 inclui somente os campos seguros tipados do plano local v2, com SHA-256. Não armazena URL/path/query, headers, payload ou JSON arbitrário do arquivo. A revisão é imutável para uma identidade de execução; não é ainda um catálogo de testes editáveis, que pertence a M3.

O relatório JSON v1 recebe o campo aditivo `windows[].encodedWindow`, base64 do `MetricWindowCodec` v1. Ele preserva dados que não existiam na apresentação JSON, como atrasos de registro por janela. Relatórios produzidos antes desse campo precisam ser gerados novamente para importação; a CLI rejeita o documento sem inventar valores. A entrada é limitada a 16 MiB e 133 janelas. Propriedades duplicadas, schemas incompatíveis, dados binários adulterados e divergências entre as janelas e os totais são rejeitados.

Cada importação grava janelas, resumo, cobertura/manifesto e resultado em uma única transação. Mesmo projeto/execução e hash canônico retornam `Replayed`; conteúdo diferente retorna conflito. Uma falha antes da selagem desfaz janelas e resumo juntos. Dados parciais permanecem parciais, com causa, falhas e qualidade preservadas. O relatório de entrada deve estar finalizado e ser JSON válido; arquivos truncados não são importáveis.

Janelas usam `bytea` canônico de 17.832 bytes. O resumo `DNSUM001` contém metadados limitados, duas distribuições binárias densas e checksum; contadores, somas e buckets são validados na faixa de `bigint`. Os percentis de consulta são derivados dos buckets consolidados, e a proporção de 429 usa o total de status finais. Nenhum percentil é somado ou calculado pela média de percentis de janelas.

As consultas sempre exigem projeto e retornam somente resultados selados. Projetos distintos podem conter o mesmo UUID de execução sem compartilhar dados. Importação de um único stream local é o limite atual; protocolo autenticado de ingestão por worker e merge distribuído pertencem a M3–M5.

## Retenção

`prune` seleciona no máximo `--batch` execuções seladas por rodada, usando locks com `SKIP LOCKED`. Não é um agendador: execute periodicamente até reduzir o backlog. Idades são contadas após `finishedUtc`:

- 7 dias: remove janelas, conservando resumo e snapshot.
- 30 dias: remove resumo e snapshot; a execução deixa de aparecer nas consultas.
- 90 dias: remove a identidade/hash residual. Depois disso, importar novamente cria uma nova entrada; não há garantia de deduplicação após a expiração.

Execuções não seladas são preservadas. Exclusões são transacionais e limitadas por execução; PostgreSQL pode reutilizar páginas sem reduzir imediatamente o arquivo físico. Backup/restore operacional, credenciais protegidas e política multiusuário continuam nos marcos posteriores.

## Validação reproduzível

Use **outro banco exclusivo para testes**, pois os testes criam triggers de falha e alteram temporariamente o registro de migrations. Nunca aponte `DINAMICO_TEST_DATABASE` para dados úteis. Sem essa variável, a categoria PostgreSQL falha claramente.

```sh
export DINAMICO_TEST_DATABASE='Host=127.0.0.1;Port=55432;Username=dinamico;Password=dinamico-local-m2;Database=dinamico_m2_test'
dotnet test Dinamico.slnx -c Release --no-build --no-restore --logger trx --results-directory TestResults/M2/Final
```

Crie previamente o banco `dinamico_m2_test` com o usuário de desenvolvimento. Para validar somente o motor sem PostgreSQL, use `dotnet test tests/Dinamico.LoadEngine.Tests -c Release --no-build --no-restore`.

O experimento sintético mede 30 importações por população, com aquecimento separado, histogramas esparsos e densos. Execute sem outro cliente escrevendo no banco de validação:

```sh
dotnet tools/Dinamico.PersistenceValidation/bin/Release/net10.0/Dinamico.PersistenceValidation.dll --output storage-evidence.json
```

Use um arquivo de saída novo. `relationBytes` inclui tabelas, índices e TOAST. `walPositionBytes` é a posição corrente de inserção de WAL **do cluster inteiro**; diferenças incluem qualquer outra atividade do cluster. Alocação em páginas, compressão e full-page images afetam os números. O experimento mede persistência local, não RPS HTTP, capacidade distribuída ou armazenamento de produção.
