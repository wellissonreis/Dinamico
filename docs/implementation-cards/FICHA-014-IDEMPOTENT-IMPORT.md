# FICHA-014 — Importação idempotente e consolidação

Prioridade: P0. Marco: M2. Status: em implementação. Autorizado em 2026-10-22.

Objetivo: importar o relatório local e consolidar as janelas exatamente uma vez.

Escopo: execução local de um único stream; importação limitada, transacional e selada; chave execução/projeto e hash canônico. Replay igual retorna confirmação; conteúdo diferente gera conflito. Validação de sequência, cobertura, manifesto, schema e contadores; somar buckets antes de derivar quantis. Resultados parciais conservam sua incompletude.

Dependência: 013. Worker autenticado, ingestão distribuída, ACK de rede e reconciliação pertencem a marcos posteriores.

Decisão: acrescentar `encodedWindow` ao relatório JSON v1 como campo aditivo, contendo o codec canônico v1 em base64. O JSON de apresentação anterior não contém os atrasos de registro de cada janela e não permite reconstrução exata; a importação exige o campo, sem inventar dados ausentes. Relatórios antigos continuam legíveis, mas precisam ser gerados novamente para importação M2.

Aceite: replay concorrente sem dupla soma; conflito de conteúdo; rollback entre inserção e resumo; rejeição de gaps e schemas incompatíveis; comparação independente dos totais; ausência de dados sensíveis persistidos.

Validação: pendente.
