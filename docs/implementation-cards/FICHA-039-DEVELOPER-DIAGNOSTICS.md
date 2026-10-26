# FICHA-039 — Diagnóstico para desenvolvedores e integração com telemetria

Origem: ficha 2 relatada pelo usuário em 2026-10-25. Prioridade: P1, após tornar a contagem confiável na 038. Status: implementada e validada no escopo HTTP local; configuração do backend e instrumentação do alvo são próprias do ambiente do operador.

## Entrega atual — 2026-10-25

Gráficos/tabelas de taxas, latências com população, falhas, memória, CPU, ThreadPool e HTTP; recursos do gerador persistidos com migração 2 e retenção; captura Off/Sampled/Complete com limite e cobertura; detalhe individual ordenável, tempo até headers e trace. Sampled retém até 1.000 operações, Complete exige até 2.000 planejadas; recursos limitados a 133 e diagnóstico JSON a 2 MiB. Após sete dias o histórico distingue detalhes removidos dos metadados da captura original, retidos com recursos por trinta dias.

Correlação W3C opt-in e exportação OTLP/HTTP JSON de spans retidos, sem dependência de backend específico. Configuração somente no host, lotes de 128, deadline total de dois segundos, sem retry ou envio de segredos/URL/header/payload. Indisponibilidade ou rejeição parcial não altera os contadores. Fixture instrumentada verificou request → endpoint → dependência lenta e receptor OTLP real; não representa configuração do alvo do usuário. A interface declara que recursos do processo não são recursos do alvo.

Testes de contrato, coleta, transporte, persistência/retenção/replay/reinício, API e navegador aprovados; comparação Off/Sampled/Complete em três repetições documentada sem promessa universal de overhead/capacidade. [Evidências e limites](../DIAGNOSTICS-DELIVERY.md); [configuração](../WEB-LOCAL.md#correlação-e-exportação-opcional). As seções seguintes preservam o planejamento e os registros de abertura anteriores à implementação.

Aceite local: 630 testes .NET e seis testes Chromium aprovados; builds Release/frontend sem erros. A correção do overflow em 390 px foi retestada com o resultado detalhado e traces; legado e retenção individual mantêm ausência explícita de dados.

Continuação operacional: [laboratório Jaeger reproduzível](../TELEMETRY-LOCAL.md) com alvo instrumentado, SDK fixado/lockfile, memória/CPU limitadas e validador real de API/traces. O painel desta sessão foi configurado para o backend local. A cadeia de IDs, opt-out, coletor indisponível e canários de segredos foram verificados no Jaeger; isso não equivale à instrumentação de uma aplicação externa não fornecida.

A pendência de duração foi validada em [ensaios de 120/600 segundos](../DIAGNOSTICS-SOAK.md), mantendo limites de captura e todas as janelas de recursos. A continuação acrescentou [correção de precisão Linux](../LINUX-SCHEDULER-DELIVERY.md); o aceite atualizado tem 635 testes em cada runtime Windows/Linux e seis de navegador com Jaeger real configurado. Versões, comandos e ressalvas de recursos permanecem nos respectivos relatórios.

## Objetivo

Permitir que o desenvolvedor identifique quando houve degradação, quais operações ficaram lentas ou falharam e quais evidências ajudam a localizar o gargalo. Entregar recursos do gerador persistidos e correlação opcional com telemetria da aplicação testada, com cobertura e custo explícitos.

## Situação verificada

- `FrontEnd/src/RunChart.tsx` recebe taxa iniciada e P95 de HTTP completo por janela; há tooltip simples por ponto. O contrato `src/Dinamico.Web/Views.cs` omite distribuição de status, motivos de descarte e recursos do processo.
- `Contracts/RequestObservation.cs` contém duração terminal individual transitória, status e categoria de erro. O resultado retém histogramas agregados, sem histórico individual de todas as requisições. Os exemplos de erro atuais são agregados por categoria/status, não traces de requests individuais.
- `Observability/BoundedRunObservability.cs` coleta working set, heap, CPU acumulada, GC, ThreadPool e métricas HTTP em resumos da CLI. `ExecutionService.cs` não conecta esse coletor à execução web; os recursos não são campos da visão web nem do histórico M2 atual.
- Recursos do processo medidos pelo gerador não representam memória/CPU do alvo. As métricas HTTP atuais observam o processo e podem incluir outro tráfego, conforme `HttpRuntimeMetrics.cs`. P95 agregado não identifica sozinho onde a aplicação travou.

## Entregas e sequência

1. **Gráfico e diagnóstico com dados existentes:** séries de taxa planejada/iniciada/completa/2xx, descartes e falhas por causa; P50/P95/P99, mínimo/máximo e população HTTP completa por janela; terminais incluindo falhas em apresentação distinta. Exibir unidades, intervalo real, contagem, cobertura e lacunas. Percentil global vem do histograma consolidado, nunca da média dos percentis das janelas. Disponibilizar tabela acessível e inspeção do intervalo.
2. **Recursos do gerador:** coletor tipado reutilizável ligado ao ciclo web/CLI, com snapshots por janela e flush final, sem depender de parsing de stdout. Persistir memória/heap, CPU, alocações/GC, ThreadPool e fila/conexões HTTP com timestamp e escopo. CPU deve declarar denominador e usar deltas de tempo; contadores cumulativos e gauges precisam de tratamento próprio. Valores indisponíveis são null; lacunas não viram zero. Resultado, histórico e download devem conservar essas informações após reinício do host.
3. **Operações individuais:** oferecer modo diagnóstico explícito, com orçamento finito de quantidade, bytes, fila e retenção definido antes da implementação. Capturar índice de chegada, início relativo monotônico, duração terminal, status/categoria e identificadores de correlação quando habilitados; permitir localizar operações lentas e falhas. Reservar amostras para ambas as populações e informar seleção, quantidade coletada/descartada e cobertura. Não apresentar amostra como duração de cada request. Para inspeção de todas as operações em ensaios pequenos, definir captura completa com limite pré-validado; se atingir limite, marcar captura parcial sem perder os contadores canônicos.
4. **Integração opcional com telemetria:** avaliar OpenTelemetry/OTLP como contrato interoperável e definir destinos, configuração, sampling e orçamento de exportação antes de adicionar dependências. Correlação com o alvo exige opt-in explícito e registro no snapshot, conforme [06](../06-OBSERVABILITY.md); verificar propagação automática do runtime para preservar esse contrato. Correlacionar execução/amostra a trace/span e permitir abrir o trace no backend configurado. Uma fixture instrumentada deve demonstrar request → endpoint → dependência lenta. Nenhum backend específico é obrigatório nesta abertura.
5. **Diagnóstico acionável:** destacar no intervalo evidências de limitação do scheduler, concorrência, espera do cliente HTTP, timeout, status e pressão de recursos. Mostrar fases somente quando medidas; duração até headers não deve ser chamada de tempo exclusivo de servidor. Para SQL, locks, filas, dependências e memória do alvo, usar instrumentação desse alvo. Na ausência, explicar o dado faltante sem inventar a localização do gargalo.

## Contratos e cuidados de implementação

Preservar [06 — Observabilidade](../06-OBSERVABILITY.md), [04 — Dados](../04-DATA-ARCHITECTURE.md) e limites de recursos existentes. Novos tipos persistidos exigem schema/migration, retenção/limpeza e tratamento explícito de execuções antigas, sem marcar ausência histórica como zero. Detalhes/amostras seguem retenção canônica de sete dias; resumos, trinta dias. O modo de captura completa precisa caber nesses limites e declarar custo/impacto no teste.

Manter agregação canônica independente de amostras/exportador: filas finitas, envio em lote fora do caminho de despacho, descarte de telemetria opcional contabilizado, cancelamento e encerramento limitados. Indisponibilidade do backend de telemetria não pode mudar a carga, habilitar retries HTTP ou converter dados de teste completos em dados parciais; a cobertura da telemetria é separada. Falhas da saída canônica continuam seguindo o contrato atual.

Não exportar payload, segredos, URL completa, mensagens livres de exception ou headers indiscriminadamente. IDs de execução/request pertencem à correlação e ao detalhe, sem labels globais de alta cardinalidade. Configuração de destino/credenciais do exportador fica no host, sem exposição no SSE/download. Medir overhead com diagnóstico desligado e ligado; não criar um único trace gigantesco para a execução inteira.

## Dependências e arquivos

[038](FICHA-038-REQUEST-LOSS-DIAGNOSIS.md) para contagem/causas; [010](FICHA-010-BOUNDED-OBSERVABILITY.md), [013–015](README.md), [030](FICHA-030-REACT-LOCAL.md) e [037](FICHA-037-RATE-LIMITING.md). Alterações previstas em `src/Dinamico.LoadEngine/{Observability,Transport,Lifecycle}`, CLI, `src/Dinamico.Web`, `src/Dinamico.Persistence` e `FrontEnd/src/{api.ts,RunChart.tsx,RunResult.tsx}`, com testes nos projetos existentes. As etapas locais não dependem de Kubernetes ou conclusão de autenticação/distribuição.

## Critérios de aceite e validação

- Gráfico/tabela permitem localizar a janela degradada e consultar status, descartes, falhas e latência com população explícita; falhas lentas não desaparecem por não terem HTTP completo.
- Memória e demais recursos do gerador aparecem ao vivo, no histórico após reinício e no download, com unidades/escopo, sem zeros artificiais. Execuções antigas continuam legíveis com campos indisponíveis.
- Fixture com operação lenta/timeout permite achar o detalhe capturado e sua duração. Captura completa pequena cobre todos os terminais; limites, saturação e sampling produzem contadores de cobertura verificáveis.
- Integração demonstra correlação a dependência lenta real em alvo instrumentado. Sem instrumentação do alvo, a UI identifica a limitação da evidência. Opt-in desligado não injeta correlação indevida.
- Backend indisponível/lento não muda contagens nem perfil da carga; fila/memória e encerramento permanecem limitados. Testar canários de segredos em banco, amostras, exportador, API/SSE e download.
- Comparar overhead em perfil repetível antes/depois e documentar orçamento/resultado. Executar testes de contrato/coleta/persistência/web pertinentes, build Release/frontend e navegador, registrando evidências reais.

## Status e decisões pendentes

Esta abertura altera apenas documentação. Escolha do backend de telemetria, orçamento numérico da captura individual e tolerância de overhead devem ser registrados durante o detalhamento técnico. Nenhuma integração, medição nova ou tela adicional foi entregue nesta etapa.

## Continuação — testes unitários em 2026-10-25

Adicionado `Process_summary_preserves_execution_window_and_resource_fields` em `tests/Dinamico.LoadEngine.Tests/BoundedObservabilityTests.cs`: verifica correlação por execução/janela, instante, motivos de descarte e presença de memória, heap, CPU, alocações, pausas GC e ThreadPool no resumo emitido, aceitando indisponibilidade explícita do runtime. Não exige valores de consumo específicos, que variam por ambiente.

`tests/Dinamico.Web.Tests/MetricProjectionTests.cs` verifica que timeout após headers mantém duração terminal de dois segundos e não entra nos percentis HTTP completos; respostas 429/500 preservam sua população e duração aproximada. Esses testes cobrem o comportamento existente, sem implementar coleta web de recursos, persistência de recursos, captura individual ou integração externa.

Validação compartilhada com a [038](FICHA-038-REQUEST-LOSS-DIAGNOSIS.md): dez casos novos, 85 testes direcionados aprovados no total e build Release sem avisos/erros após restore locked-mode. Não houve validação de banco, navegador ou backend de telemetria. As entregas desta ficha permanecem pendentes.


## Continuação — URLs e retenção automática em 2026-10-26

Atendendo ao uso de URLs como `https://app-hub.cplus.com.br/`, o validador aceita domínios HTTP/HTTPS sem resolver DNS na validação. O transporte usa resolução DNS do handler, timeout de conexão e validação TLS padrão, sem seguir redirecionamentos; IPs literais mantêm a política de loopback. A interface e o guia WEB-LOCAL explicam os dados persistidos, correlação e a separação entre PostgreSQL e backend de spans.

`RetentionService` aplica `PostgresRunStore.PruneAsync` ao iniciar o painel e a cada minuto: detalhes/janelas por 7 dias, resumos por 30 dias e identidades por 90 dias, em lotes limitados. A gravação de diagnósticos continua pela importação canônica já existente. Não há coleta de corpos ou headers das respostas.

Validação: build React/TypeScript aprovado; 120 testes direcionados de validação/transporte/CLI, um teste adicional com DNS localhost e um teste da API aprovados. API compilada em Release. Banco de teste não configurado: exclusão real por retenção e navegação não foram revalidadas nesta continuação. Nenhuma carga foi enviada ao domínio informado.
