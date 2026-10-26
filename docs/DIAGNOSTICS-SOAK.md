# Ensaios prolongados de diagnóstico

Execução em 2026-10-25, Windows x64, 16 processadores, .NET runtime 10.0.13; engine 0.1.0.0. A CLI Release existente tinha SHA-256 `3f4b4b9615da1cead22f684486dbb84215b4822d04214c18bc466f4e5a1ff289`; revisão do repositório `5a83f0cb3352fcb8252a6d792eb300e478b3a3ab`. Nenhum build ou binário foi alterado durante os ensaios.

Cada comando foi executado sequencialmente em janela tmux própria, com `--quick` e sem `--require-gates`. O modo quick adiciona warmup de 2 s a 100 RPS; o warmup não entra nos resultados do cenário.

```sh
dotnet.exe tools/Dinamico.LocalValidation/bin/Release/net10.0/Dinamico.LocalValidation.dll . --quick --durations 120 --repetitions 1 --rps 1000 --capture-mode Off --output TestResults/Diagnostics/SoakOff120
dotnet.exe tools/Dinamico.LocalValidation/bin/Release/net10.0/Dinamico.LocalValidation.dll . --quick --durations 120 --repetitions 1 --rps 1000 --capture-mode Sampled --output TestResults/Diagnostics/SoakSampled120
dotnet.exe tools/Dinamico.LocalValidation/bin/Release/net10.0/Dinamico.LocalValidation.dll . --quick --durations 600 --repetitions 1 --rps 1000 --capture-mode Sampled --output TestResults/Diagnostics/SoakSampled600
```

| Cenário | Funcional | Recebidas = iniciadas = terminais | Planejadas | Drops / percentual | RPS atingidas | Cobertura de recursos | Captura observada / retida / descartada | Artefatos |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Off, 120 s | sim | 119.888 | 120.000 | 112 / 0,0933% | 999,06 | 25/25 janelas | 119.888 / 0 / 119.888 | 1,032 MiB |
| Sampled, 120 s | sim | 119.878 | 120.000 | 122 / 0,1017% | 998,97 | 25/25 janelas | 119.878 / 732 / 119.146 | 1,208 MiB |
| Sampled, 600 s | sim | 599.726 | 600.000 | 274 / 0,0457% | 999,54 | 121/121 janelas | 599.726 / 733 / 598.993 | 4,338 MiB |

Todos os relatórios confirmam `received = started = terminal`, `noInFlight`, resposta HTTP 202 para cada operação iniciada, cobertura completa e `ExitCode=0`. Os três cenários tiveram qualidade `GeneratorLimited`, logo o gate de capacidade sem drops falhou. Os contadores atribuem os drops ao agendador: Off120 teve 25 por tolerância de atraso e 87 por capacidade de wake; Sampled120 teve 31 e 91; Sampled600 teve 63 e 211. Não se conclui capacidade máxima a partir destes ensaios.

## Recursos e captura

| Cenário | CPU média (% de um núcleo) | RSS máximo / final (MiB) | Heap gerenciado máximo (MiB) | Pausa GC cumulativa final (ms) | Máximos amostrados de threads / fila |
|---|---:|---:|---:|---:|---:|
| Off120 | 18,831 | 69,426 / 65,582 | 19,058 | 29,689 | 16 / 0 |
| Sampled120 | 18,709 | 69,727 / 66,152 | 18,541 | 29,945 | 16 / 1 |
| Sampled600 | 17,405 | 70,219 / 69,395 | 22,422 | 125,642 | 16 / 1 |

O relatório de recursos registrou RSS (working set), heap gerenciado, heap GC, contagem de handles do processo e conexões HTTP. Valores de crescimento na segunda metade são a estatística descritiva calculada pelo relatório; cada sequência terminou após drenagem, por isso conexões atuais terminaram em zero. RSS mudou +0,18%, +0,19% e +0,97% na segunda metade, respectivamente. Heap gerenciado mudou −28,86%, +61,71% e −19,27%; heap GC mudou +43,89%, +25,59% e +18,79%; handles mudaram −2,24%, +2,01% e −2,92%. Conexões HTTP tiveram pico estável de 17, 16 e 16. As séries têm 25/25/121 amostras sem métricas ausentes. Isso descreve estes períodos e não demonstra plateau de memória.

Sampled reteve 732 e 733 eventos, abaixo do limite de 1.000. `captureComplete=false` é esperado porque a amostragem descarta eventos observados. Os diretórios de evidência permanecem em `TestResults/Diagnostics/SoakOff120`, `SoakSampled120` e `SoakSampled600`; cada um ficou abaixo de 16 MiB.

## Limitações

Os ensaios foram sequenciais entre si, mas houve atividade concorrente no mesmo host: serviço Docker Jaeger/telemetria e web, além de um teste separado da API/demo. A validação Linux/WSL do root também rodou VSTest direcionado e três execuções Sampled de 10 s em paralelo durante parte do Sampled600. Essa concorrência pode influenciar latência, drops e uso de recursos. Há apenas uma execução por modo/duração; a diferença entre Off120 e Sampled120 não mede overhead causal. O resultado também não estabelece capacidade máxima nem plateau de memória.

A correção posterior da espera Linux e sua validação isolada estão em [LINUX-SCHEDULER-DELIVERY](LINUX-SCHEDULER-DELIVERY.md). Os ensaios desta página usaram o binário Windows anterior à correção Linux; o caminho de espera Windows foi preservado.
