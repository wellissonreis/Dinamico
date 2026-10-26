# Espera de alta resolução no Linux/WSL

Em 2026-10-25, os ensaios de continuação da ficha 038 reproduziram aproximadamente metade das intenções sem início, mesmo contra fixture HTTP rápida. Não houve perda entre envio e recebimento: todas as iniciadas chegaram à fixture e terminaram em 202. O problema medido era limitação da espera do gerador Linux/WSL, anterior à aplicação alvo.

## Evidência e correção

SDK 10.0.204, runtime Linux 10.0.8, WSL x64, perfil 1.000 req/s, C=32, timeout de dois segundos, captura Sampled, três execuções de dez segundos; warm-up separado de dois segundos excluído. Nenhum timer global, quantum, tolerância, teto por despertar ou política HTTP foi alterado.

| Espera | Repetição | Planejadas | Iniciadas = recebidas = completas | Não iniciadas | Taxa iniciada |
|---|---:|---:|---:|---:|---:|
| Gerenciada anterior | 1 | 10.000 | 4.930 | 5.070 | 49,30% |
| Gerenciada anterior | 2 | 10.000 | 4.895 | 5.105 | 48,95% |
| Gerenciada anterior | 3 | 10.000 | 4.957 | 5.043 | 49,57% |
| Nativa nova | 1 | 10.000 | 9.975 | 25 | 99,75% |
| Nativa nova | 2 | 10.000 | 9.974 | 26 | 99,74% |
| Nativa nova | 3 | 10.000 | 9.971 | 29 | 99,71% |

Antes, 2.467/2.450/2.480 despertares em dez segundos explicavam a grade observada próxima de 4 ms. De cada grupo de aproximadamente quatro intenções vencidas, o teto de despacho permitia duas. Os descartes por teto foram 4.972/4.943/4.976; os restantes foram por tolerância/fronteira. Uma medição independente de `time.sleep(1 ms)` no mesmo Linux obteve média de 1,075 ms, sugerindo que o kernel suportava espera mais fina que o caminho gerenciado usado.

`SchedulerWait` agora usa `clock_nanosleep(CLOCK_MONOTONIC)` para intervalos positivos até dez milissegundos em processos Linux de 64 bits. A ABI `timespec` tem dois campos de 64 bits, preservando os ticks de 100 ns de entrada. A syscall retorna o código de erro diretamente; `EINTR` é tratado como despertar antecipado para a agenda reler seu relógio absoluto. [Contrato POSIX/Linux](https://man7.org/linux/man-pages/man2/clock_nanosleep.2.html).

A chamada bloqueante curta roda em `Task.Run`; o chamador continua assíncrono e cancelável. O erro de uma tarefa tardia é observado. Cancelar o await não interrompe uma syscall já iniciada: ela termina posteriormente, sem enviar HTTP ou manter handles próprios. O limite de dez milissegundos é o intervalo solicitado, não garantia de tempo de parede sob atraso do SO. Em execução normal, a agenda faz uma espera serial por despertar; cancelamento seguido de reutilização pode sobrepor brevemente esperas antigas. Esperas maiores e Linux 32 bits mantêm o caminho gerenciado. Windows mantém o timer existente.

O caminho novo usa um worker do pool durante a espera frequente. Sob disputa, quota ou muitos schedulers, ainda pode haver atraso e descartes: `GeneratorLimited` continua válido. O gate original de capacidade exige **zero descartes** e não passou nessas três rodadas; a correção elimina a perda sistemática de metade neste perfil, sem prometer taxa exata ou capacidade universal. O requisito de pelo menos 99% iniciado foi atendido nas três repetições.

O ensaio posterior de 120 segundos teve **119.931 iniciadas = recebidas = completas de 120.000**, com 69 não iniciadas (0,0575%) e dados completos. O diagnóstico dos polls registrou 41 descartes por tolerância e 26 por teto; as duas intenções restantes foram classificadas no encerramento pelo controlador. Os contadores canônicos conciliam todas as 120.000 intenções, enquanto os detalhes do scheduler descrevem seus polls. CPU entre amostras: 41,230% de um núcleo; RSS máximo/final 90,953 MiB, crescimento descritivo da segunda metade 1,39%. Em 25 amostras, threads do pool variaram de cinco a 17 e terminaram em seis; a fila amostrada teve máximo um e final zero. Não houve evidência de backlog crescente nessas amostras; elas não provam ausência de picos entre coletas nem plateau de memória.

## Reprodução e isolamento dos binários

O SDK Linux foi instalado apenas em `/tmp/dinamico-linux-sdk` com SHA-512 conferido contra os metadados oficiais; [instalação manual documentada pela Microsoft](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual). Os artefatos Linux foram compilados com `--artifacts-path /tmp/dinamico-linux-artifacts`, sem sobrescrever os binários Windows que estavam no ensaio prolongado. O utilitário recebeu `--engine` para apontar explicitamente a CLI isolada.

```sh
/tmp/dinamico-linux-sdk/dotnet restore Dinamico.slnx --locked-mode --artifacts-path /tmp/dinamico-linux-artifacts
/tmp/dinamico-linux-sdk/dotnet build Dinamico.slnx -c Release --no-restore --artifacts-path /tmp/dinamico-linux-artifacts
/tmp/dinamico-linux-sdk/dotnet build tools/Dinamico.LocalValidation/Dinamico.LocalValidation.csproj -c Release --artifacts-path /tmp/dinamico-linux-artifacts
DOTNET_HOST_PATH=/tmp/dinamico-linux-sdk/dotnet /tmp/dinamico-linux-sdk/dotnet /tmp/dinamico-linux-artifacts/bin/Dinamico.LocalValidation/release/Dinamico.LocalValidation.dll . --quick --durations 10 --repetitions 3 --rps 1000 --capture-mode Sampled --engine /tmp/dinamico-linux-artifacts/bin/Dinamico.LoadEngine.Local/release/Dinamico.LoadEngine.Local.dll --output TestResults/Diagnostics/LinuxNativeWait
```

Use diretório de saída novo a cada repetição. Os manifestos em `TestResults/Diagnostics/LinuxSampled` (antes), `LinuxNativeWait` e `LinuxNativeWait120` identificam hashes, ambiente, contagens independentes, cobertura e recursos. Os ensaios compartilham hardware com serviços locais e outras verificações; não isolam custo causal de CPU nem equivalem à execução original do usuário sem seus parâmetros.

## Validação final

**635 testes aprovados em cada plataforma**, Windows runtime 10.0.13 e Linux runtime 10.0.8, SDK 10.0.204; builds Release sem avisos/erros. Dez testes de persistência e nove de API em cada suíte usam bancos PostgreSQL exclusivos. Os testes da espera verificam ausência de ofertas antecipadas, limites de 100 ns/submilissegundo/10 ms, retorno ao caminho gerenciado e cancelamento/reuso. Não exigem exatidão de timer em CI. A revisão delimitada da implementação não encontrou impedimentos.

A primeira suíte Linux com artefatos isolados encontrou 21 falhas de localização da CLI no harness, que pressupunha `bin/Release/net10.0` dentro do repositório. `CliTestAssembly` passou a resolver também o executável irmão no layout `--artifacts-path`, preservando todos os asserts. Depois da correção, a suíte completa passou sem testes ignorados. Evidências em `TestResults/Diagnostics/LinuxFinal` e `WindowsFinal`; os 50 testes direcionados iniciais estão em `LinuxWaitDirected`. Seis testes de navegador Windows passaram novamente com o backend Jaeger configurado.

Para repetir o ensaio prolongado, use o mesmo comando acima com `--durations 120 --repetitions 1` e outra saída. Para a suíte Linux, use `dotnet test Dinamico.slnx -c Release --no-build --no-restore --artifacts-path /tmp/dinamico-linux-artifacts -m:1`, com `DINAMICO_TEST_DATABASE` apontando para banco exclusivo e `DOTNET_HOST_PATH` para o host Linux. Caminhos `/tmp` são apenas os usados nesta sessão, não uma instalação permanente exigida pelo projeto.
