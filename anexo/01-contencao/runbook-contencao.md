# Runbook de contenção: antes da janela das 14h30

Objetivo: chegar às 14h30 sem repetir o incidente de ontem, usando apenas mudanças reversíveis em minutos e sem deploy de código no caminho crítico.

Variáveis usadas nos comandos (preencher antes de começar):

```bash
export AWS_REGION=sa-east-1
export CLUSTER=portal-distribuidor
export API_SERVICE=api-ordens
export INTEGRADOR_SERVICE=integrador
export REST_API_ID=xxxxxxxxxx
export STAGE=prod
export QUEUE_URL=https://sqs.sa-east-1.amazonaws.com/123456789012/boletas-pendentes
export DLQ_URL=https://sqs.sa-east-1.amazonaws.com/123456789012/boletas-pendentes-dlq
export ACCESS_LOG_GROUP_ARN=arn:aws:logs:sa-east-1:123456789012:log-group:/apigw/portal-distribuidor/access
```

Regra geral: antes de cada mudança, registrar o valor atual no canal do incidente (é o comando de rollback).

## Ordem de execução

Prazos relativos ao corte (T = 14h30). Se houver menos tempo, a ordem de prioridade é 1, 3, 6, 5, 4; o passo 2 só entra se a análise do deploy já estiver pronta.

| Prazo | Passo | Responsável |
|---|---|---|
| T-75 min | 1. Congelar deploys | Tech lead |
| T-70 min | 2. Decidir rollback do deploy das 13h50 (ver `02-deploy-1350`) | Eu + tech lead |
| T-65 min | 3. Limitar a prévia de enquadramento | Eu, com aval do PO |
| T-60 min | 4. Escalar a API para 4 tasks | Eu |
| T-60 min | 5. Visibility timeout da fila para 180 s | Eu |
| T-60 min | 6. Confirmar access logs do API Gateway | Eu |
| T-55 min | 7. Avisar Relacionamento | Eu |
| T-45 a T+15 | 8. Sala de guerra | Squad |

## 1. Congelar deploys até as 15h

Se o pipeline for CodePipeline:

```bash
aws codepipeline disable-stage-transition \
  --pipeline-name portal-distribuidor-api --stage-name Deploy \
  --transition-type Inbound --reason "Congelamento: janela de corte 23/10"
# repetir para integrador e front

# rollback (15h):
aws codepipeline enable-stage-transition \
  --pipeline-name portal-distribuidor-api --stage-name Deploy --transition-type Inbound
```

Se for GitHub Actions: adicionar revisor obrigatório no environment `production` até as 15h. Avisar no canal da squad.

## 2. Rollback do deploy das 13h50

Seguir `02-deploy-1350/analise-deploy.md`. Só reverter se o diff tocou o caminho crítico e o rollback for trocar a revisão da task definition.

## 3. Limitar a prévia de enquadramento

Pré-condição: o PO confirma que a validação de enquadramento que vale é a feita no envio da ordem e que a prévia é só informativa.

Preferência 1, feature flag (se existir): desligar a flag da prévia.

Preferência 2, throttling da rota no API Gateway (REST API). O caminho do recurso usa `~1` no lugar de `/`:

```bash
# registrar valor atual
aws apigateway get-stage --rest-api-id $REST_API_ID --stage-name $STAGE \
  --query 'methodSettings' > methodSettings-antes.json

aws apigateway update-stage --rest-api-id $REST_API_ID --stage-name $STAGE \
  --patch-operations \
    op=replace,path=/~1enquadramento~1previa/POST/throttling/rateLimit,value=30 \
    op=replace,path=/~1enquadramento~1previa/POST/throttling/burstLimit,value=60
```

Se for HTTP API:

```bash
aws apigatewayv2 update-stage --api-id $REST_API_ID --stage-name $STAGE \
  --route-settings '{"POST /enquadramento/previa":{"ThrottlingRateLimit":30,"ThrottlingBurstLimit":60}}'
```

O limite inicial (30 req/s) sai da contagem por rota em um dia normal antes da prévia existir. Recalibrar com o número real. Requisições acima do limite recebem 429; a tela mostra a prévia como indisponível, o envio da ordem continua funcionando.

Não fazer: limitar o POST /ordens.

## 4. Escalar a API de 2 para 4 tasks com `Max Pool Size=50`

Manter o teto de conexões: 4 tasks × 50 = 200, igual a 2 × 100 hoje. Escalar sem reduzir o pool dobraria a concorrência possível no banco saturado, o mesmo efeito que proibimos ao vetar o aumento de `Max Pool Size`. A mudança é uma nova revisão da task definition alterando só a connection string (sem código), feita antes de T-60.

```sql
-- no RDS
SHOW max_connections;
SELECT count(*) FROM pg_stat_activity;
```

Só seguir se `4 x 50 + conexões de outros clientes (integrador, Lambda, admin)` ficar abaixo de 80% de `max_connections`. Se não couber, escalar para 3.

```bash
aws ecs describe-services --cluster $CLUSTER --services $API_SERVICE \
  --query 'services[0].desiredCount'

aws ecs update-service --cluster $CLUSTER --service $API_SERVICE --desired-count 4

# rollback (depois das 15h):
aws ecs update-service --cluster $CLUSTER --service $API_SERVICE --desired-count 2
```

Revisão de configuração, sem código; rollback = voltar para a revisão anterior e `--desired-count 2`. Limitação conhecida: não resolve um banco saturado. Protege contra cascata de health check e saturação das tasks. O freio real é o passo 3.

Não fazer: aumentar `Max Pool Size`, escalar o RDS verticalmente (provoca failover).

## 5. Visibility timeout de `boletas-pendentes` para 180 s

```bash
aws sqs get-queue-attributes --queue-url $QUEUE_URL \
  --attribute-names VisibilityTimeout RedrivePolicy MessageRetentionPeriod

aws sqs set-queue-attributes --queue-url $QUEUE_URL \
  --attributes VisibilityTimeout=180
```

Vale para os próximos recebimentos. Conferir se o integrador não sobrescreve o valor no `ReceiveMessage` (parâmetro `VisibilityTimeout` no código). Se sobrescrever, a mudança da fila não tem efeito e o passo vira uma variável de ambiente do integrador, o que exige novo deploy: nesse caso, não fazer antes da janela.

Custo da mudança: uma mensagem que falha de verdade demora até 3 minutos para ser tentada de novo, em vez de 30 s.

## 6. Access logs do API Gateway com horário de chegada

```bash
aws apigateway get-stage --rest-api-id $REST_API_ID --stage-name $STAGE \
  --query 'accessLogSettings'
```

Se não estiver ativo, ou se o formato não tiver `requestTimeEpoch`:

```bash
FORMAT='{"requestId":"$context.requestId","requestTime":"$context.requestTime","requestTimeEpoch":"$context.requestTimeEpoch","httpMethod":"$context.httpMethod","resourcePath":"$context.resourcePath","status":"$context.status","integrationLatency":"$context.integrationLatency","responseLatency":"$context.responseLatency","principalId":"$context.authorizer.principalId","sourceIp":"$context.identity.sourceIp"}'

aws apigateway update-stage --rest-api-id $REST_API_ID --stage-name $STAGE \
  --patch-operations \
    op=replace,path=/accessLogSettings/destinationArn,value=$ACCESS_LOG_GROUP_ARN \
    "op=replace,path=/accessLogSettings/format,value=$FORMAT"
```

Não registrar corpo da requisição nem dados do investidor no access log.

## 7. Aviso para Relacionamento (enviar em T-55 min)

> Hoje estamos com a equipe técnica monitorando o portal durante a janela de corte. Se algum distribuidor não receber confirmação na tela ao enviar uma ordem, a orientação é consultar a lista de ordens antes de reenviar. Reenvios sem conferência podem gerar ordens em duplicidade.

## 8. Sala de guerra (13h45 às 14h45)

Painel: `03-timeline/dashboard.json`.

| Sinal | Limite | Ação |
|---|---|---|
| p95 POST /ordens | > 1 s por 2 min | Baixar o throttling da prévia para 5 req/s |
| p95 POST /ordens | > 3 s | Bloquear a prévia (rateLimit=0) |
| `DatabaseConnections` | > 80% do orçamento | Bloquear a prévia |
| CPU do RDS | > 85% por 3 min | Bloquear a prévia |
| `UnHealthyHostCount` | > 0 | Investigar health check; não reiniciar tasks no escuro |
| `ApproximateAgeOfOldestMessage` | > 5 min | Registrar; não mexer no integrador antes das 14h30 |
| DLQ | crescendo | Registrar; não fazer redrive |

Às 14h45: registrar no canal o pico de cada métrica e se alguma ação foi disparada.
