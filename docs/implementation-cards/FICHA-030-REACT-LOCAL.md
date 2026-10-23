# FICHA-030 — Interface React local e integrações 029/031/032

Prioridade: P0. Marco: recorte local de M6. Status: recorte local concluído em 2026-10-22. Marco M6 completo permanece pendente. Autorizado nesta data.

Objetivo: oferecer interface simples para configurar carga constante, acompanhar execução real, cancelar e consultar resultados M2.

Escopo: React/TypeScript/Vite em `FrontEnd`; adaptador ASP.NET em `src/Dinamico.Web` para o motor existente e PostgreSQL; SSE de snapshots limitados, histórico paginado e download da visão pública. Layout responsivo, estados vazios/de erro e qualidade/completude explícitas. Projeto lógico salvo no navegador; nenhuma URL, credencial ou corpo persistido no browser ou nos resultados.

Dependências efetivamente usadas: M1 e M2. A autorização do frontend antecipa somente o recorte local das 029–032; não encerra M3–M6 completos. Login, sessões, cofre, grants remotos, workers distribuídos e perfil em degraus 033 permanecem pendentes. Não simular autenticação ou dados ao vivo.

Decisões: servidor restrito ao loopback; host e origem validados; POST exige header do cliente local. Uma execução ativa por processo e cache limitado a 20 finalizadas/133 janelas cada. Métricas live são agregadas em janelas de 5 s; SSE reconecta pelo snapshot, sem somar deltas novamente. Resumo final é persistido com a importação canônica M2; falha de banco fica visível e o resultado em memória pode ser baixado. Não equivale a recuperação de uma execução após crash de processo (M3).

Aceite: configurar/executar/acompanhar/cancelar/histórico funcionando contra motor/banco reais; refresh/reconexão sem dupla carga; rejeição de alvo externo e origem indevida; canários seguros; mobile sem overflow; nenhum dado fictício usado no produto. Build frontend e solução Release; testes Kestrel/PostgreSQL e navegador.

Validação: 585 testes .NET aprovados (575 motor/CLI + 7 persistência + 3 API), Release sem avisos/erros; React/TypeScript build aprovado e 3 testes Chromium reais aprovados. Execução, SSE, histórico/download, cancelamento, rejeição de alvo externo, estado de banco indisponível, mobile sem overflow, ajuda e projeto verificados. Primeiro ensaio de navegador expôs falha na localização do build quando iniciado pelo diretório do projeto; corrigida e retestada. Um seletor de teste ambíguo foi corrigido para buscar a navegação explicitamente. Evidências em `TestResults/Web`; comandos em [WEB-LOCAL](../WEB-LOCAL.md). Nenhum aceite de autenticação, execução remota, distribuição ou degraus foi inferido.
