# Anexo: material de execução

O documento de raciocínio está no [`README.md` da raiz](../README.md). Esta pasta é apoio opcional para a execução do plano. Nada aqui foi executado contra o ambiente: não há acesso ao AWS, ao banco nem ao repositório do portal. Cada pasta traz o que deve ser rodado, por quem e em que ordem. Nomes de recursos (cluster, filas, API, instância RDS) e de tabelas e colunas são suposições a ajustar ao ambiente real.

| Pasta | Item do plano | Quando |
|---|---|---|
| [`01-contencao`](01-contencao/) | Runbook até a janela das 14h30: congelamento, prévia limitada, escala, visibility timeout, access logs, sala de guerra | Antes do corte |
| [`02-deploy-1350`](02-deploy-1350/) | Como decidir se o deploy das 13h50 é causa e se vale reverter | Antes do corte |
| [`03-timeline`](03-timeline/) | Painel do CloudWatch, consultas do Logs Insights e do banco, matriz de hipóteses | Depois do corte |
| [`04-dlq`](04-dlq/) | Export da DLQ para o S3 sem apagar nada e classificação das 1.900 mensagens | Hoje |
| [`05-duplicatas`](05-duplicatas/) | Duplicatas separadas em dois tipos e comunicado para Operações | Hoje, antes do cálculo da cota |
| [`06-casos-37`](06-casos-37/) | Cruzamento de chegada e registro, impacto por ordem, comunicados para Relacionamento | Entrega amanhã às 12h |
| [`07-raiz/corte`](07-raiz/corte/) | Horário de corte decidido pela chegada da requisição | Correção de raiz |
| [`07-raiz/idempotencia`](07-raiz/idempotencia/) | `Idempotency-Key` e outbox no POST /ordens | Correção de raiz |
| [`07-raiz/consulta-posicoes`](07-raiz/consulta-posicoes/) | Consulta reescrita, índices, prévia em lote, pools separados | Correção de raiz |
| [`07-raiz/integrador`](07-raiz/integrador/) | Consumidor idempotente com encerramento gracioso e despachante de webhooks | Correção de raiz |
| [`07-raiz/carga`](07-raiz/carga/) | Teste de carga k6 com o perfil do pico, usado como gate de release | Correção de raiz |
| [`07-raiz/portal-minimo`](07-raiz/portal-minimo/) | Projeto .NET mínimo que compila e testa o C# das três pastas acima, com Postgres em container | Correção de raiz |
| [`08-proativo`](08-proativo/) | Segredos e LGPD, alarmes e SLO, correlation ID, entrega segura | Depois de estabilizar |

## O que foi verificado

- Código C# (`07-raiz/corte`, `idempotencia`, `consulta-posicoes`): compilado e testado pelo projeto de `07-raiz/portal-minimo` (`dotnet test`, 18 testes passando, com Postgres em container e SQS simulado). Os tipos do portal que não existem aqui (`PortalDbContext`, `Ordem`, `RegraDeEnquadramento` e outros) foram reimplementados no mínimo, então isso prova a lógica do anexo, não a integração com o portal real. O teste achou um defeito no `OutboxPublisher` (transação explícita com retry ligado), já corrigido.
- `07-raiz/integrador`: checagem de tipos e 12 testes passando (`npm test`). Os testes cobrem entrega duplicada simultânea, timeout ambíguo com e sem a ordem no destino, rejeição, erro transitório com backoff, heartbeat, encerramento gracioso, mensagem inválida e assinatura de webhook.
- `04-dlq/export-dlq.ts`: checagem de tipos contra o SDK atual da AWS.
- `03-timeline/dashboard.json` e `08-proativo/alarmes.yaml`: sintaxe válida.

## O que não foi verificado

- SQL: não foi executado; depende do schema real.
- Comandos da AWS CLI e o template de alarmes: não foram aplicados; o template não passou por `cfn-lint`.
