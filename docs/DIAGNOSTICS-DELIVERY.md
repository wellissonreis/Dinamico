# Diagnóstico local — entrega de 2026-10-25

As fichas 038/039 entregam funil e causas de requisições, gráfico/tabelas por janela, recursos do gerador persistidos, captura individual opcional e correlação/exportação OTLP. [Configuração e interpretação](WEB-LOCAL.md#diagnóstico-de-requisições-e-recursos).

## Conclusão sobre as requisições ausentes

O motor pode descartar intenções **antes de enviar HTTP**, por atraso/teto do scheduler ou concorrência. A interface anterior escondia essas causas num total. Os testes controlados demonstram exatamente 500 inícios/500 descartes por teto ao acordar a cada 4 ms com quantum de 1 ms a 1.000 req/s, mesmo com transporte imediato; outro caso demonstra 50/50 por concorrência. Todos os inícios têm um único terminal; os descartes não são requests que chegaram ao alvo.

No ensaio real desta entrega, todas as iniciadas foram recebidas pela fixture e concluídas com 202. Não houve desaparecimento entre cliente e servidor, timeout ou falha de transporte. Os descartes medidos foram de scheduler. Isso não reproduz necessariamente o teste original do usuário: sem seus parâmetros e resultado, a causa daquela execução continua pendente. Nenhum algoritmo de agenda, teto, tolerância ou política de retry foi alterado para esconder descartes.

## Ensaios e custo da captura

Windows x64, .NET 10.0.13, SDK 10.0.204, 16 processadores lógicos. Fixture loopback 202 com 16 bytes, mesmo host e processo separado do gerador. C=32, timeout de dois segundos, quantum 1 ms, tolerância 5 ms. Cada linha representa três execuções de cinco segundos, processos novos; warm-up separado de dois segundos excluído. Sem correlação/exportador nos ensaios de captura. Não são ensaios prolongados nem estimativa de capacidade máxima.

| Captura / taxa | Iniciadas em cada repetição | Descartes em cada repetição | CPU média (% de um núcleo, primeira amostra) | Working set final médio (MiB) | Alocados cumulativos médios (MiB) |
|---|---|---|---:|---:|---:|
| Off / 10 | 50, 50, 50 | 0, 0, 0 | 12,154 | 52,10 | 2,98 |
| Sampled / 10 | 50, 50, 50 | 0, 0, 0 | 11,325 | 52,31 | 3,01 |
| Off / 100 | 499, 499, 499 | 1, 1, 1 | 17,036 | 55,96 | 5,19 |
| Sampled / 100 | 499, 499, 499 | 1, 1, 1 | 18,182 | 56,25 | 5,30 |
| Complete / 100 | 499, 498, 499 | 1, 2, 1 | 17,765 | 55,49 | 5,23 |
| Off / 1.000 | 4.966, 4.960, 4.953 | 34, 40, 47 | 34,087 | 69,61 | 25,49 |
| Sampled / 1.000 | 4.936, 4.955, 4.947 | 64, 45, 53 | 34,811 | 70,13 | 26,32 |

Conciliação e cobertura passaram em todos os cenários, incluindo warm-ups. O gate de capacidade **não passou** nas taxas 100/1.000 porque exige zero descartes. A 10 req/s passou nas duas configurações; isso preserva o alcance do envelope anterior, sem ampliá-lo. A diferença observada Sampled−Off a 1.000 req/s foi +0,724 ponto percentual de CPU de um núcleo, +0,52 MiB de working set final médio e +0,83 MiB de alocação cumulativa média. A ordem foi sequencial por modo; ruído, startup/JIT e outras cargas do host não foram isolados, portanto não atribuir toda diferença causalmente à captura.

O orçamento implementado é de até 1.000 amostras deduplicadas em Sampled, até 2.000 requests planejados em Complete, 133 recursos e 2 MiB de JSON de diagnóstico. A Sampled reteve 683/662/675 operações a 1.000 req/s, respeitando o limite; os relatórios completos tinham em média 194,6 KiB contra 65,7 KiB em Off. A Complete preservou todos os 499/498/499 terminais observados; não capturou as intenções descartadas antes do início. Ensaios curtos não comprovam plateau de memória ou um limite universal de overhead.

Comandos após build Release da CLI e de `tools/Dinamico.LocalValidation`:

```sh
dotnet.exe tools/Dinamico.LocalValidation/bin/Release/net10.0/Dinamico.LocalValidation.dll . --quick --durations 5 --repetitions 3 --rps 10,100,1000 --capture-mode Off --output TestResults/Diagnostics/CalibrationOff
dotnet.exe tools/Dinamico.LocalValidation/bin/Release/net10.0/Dinamico.LocalValidation.dll . --quick --durations 5 --repetitions 3 --rps 10,100,1000 --capture-mode Sampled --output TestResults/Diagnostics/CalibrationSampled
dotnet.exe tools/Dinamico.LocalValidation/bin/Release/net10.0/Dinamico.LocalValidation.dll . --quick --durations 5 --repetitions 3 --rps 100 --capture-mode Complete --output TestResults/Diagnostics/CalibrationComplete
```

Cada diretório contém manifestos com hashes dos binários, configurações sanitizadas, relatórios, contagem independente da fixture, invariantes e recursos. Foram medidos antes dos ajustes finais de limite de espera do exportador, isolamento de falha do observador opcional e validação cruzada na importação; esses ajustes não alteram a agenda nem seleção/limites de captura. Os testes finais validam os binários posteriores.

## Persistência, telemetria e validação

Migração 2 aditiva com checksum, importação atômica/idempotente e diagnóstico tipado validado contra a contagem canônica. Requests expiram em sete dias; recursos/scheduler/metadados em trinta. Histórico antigo mostra ausência; após retenção de detalhe a UI diferencia captura original e conteúdo disponível.

A correlação exige opt-in. Testes de socket comprovam ausência de propagação ambiente sem opt-in e IDs W3C explícitos quando habilitada. `TelemetryCorrelationIntegrationTests` executa endpoint instrumentado, dependência lenta controlada e receptor OTLP HTTP real, verificando a cadeia de IDs; não afirma que o backend externo do usuário foi configurado. Exportação limitada a lotes de 128 e prazo total de dois segundos; erros, rejeição parcial, resposta excessiva e destinos indisponíveis ficam separados dos dados do teste. Nenhum segredo, URL, header ou payload é exportado.

Validação final Windows: **630 testes .NET aprovados, zero falhas/ignorados** (611 motor/CLI, dez persistência, nove web). Build Release sem avisos/erros. Os testes cobrem captura limitada, população de timeout, contagem 429/500, recursos, replay/conflito, rollback, checksums, retenção, reinício da API e diagnóstico corrompido. O observador opcional que lança exception e o exportador que ignora cancelamento não invalidam os dados canônicos nem bloqueiam o encerramento. TRX em `TestResults/Diagnostics/Acceptance`.

Testes de navegador: **6/6 aprovados em Chromium Windows**, incluindo execução/SSE, reconexão, download/histórico, captura/correlação, layout, legado e detalhes removidos. Build frontend aprovado; screenshots em `TestResults/Web`. O ensaio adicional de resultado detalhado em 390 px expôs overflow horizontal: o rótulo acessível absoluto do cabeçalho do histórico escapava de seu contexto. Corrigido com posicionamento relativo no cabeçalho, preservando scroll local das tabelas e a asserção original. A suíte inteira passou novamente; `mobile-diagnostics.png` e `desktop-diagnostics.png` revisados. Nenhum aceite em outros browsers, Linux ou backend externo de produção é inferido.

Comandos do aceite: `dotnet.exe build Dinamico.slnx --configuration Release --no-restore`; com `DINAMICO_TEST_DATABASE` apontando para banco exclusivo, `dotnet.exe test Dinamico.slnx --configuration Release --no-build --no-restore -m:1 --logger trx --results-directory TestResults/Diagnostics/Acceptance`; `npm run build --prefix FrontEnd`; no diretório `FrontEnd`, `cmd.exe /c node node_modules/@playwright/test/cli.js test`. Instalação inicial do Chromium headless foi necessária neste host; a primeira tentativa não abriu browser, e não foi contada como validação da aplicação.
