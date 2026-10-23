# Segredos e dados de investidores

Prioridade 1 da agenda própria: risco de dados e legal, esforço baixo, dano irreversível se vazar.

## Credencial do banco em texto plano

As revisões de task definition são imutáveis e ficam visíveis para qualquer um com `ecs:DescribeTaskDefinition`. Mover a senha para o Secrets Manager sem rotacionar não resolve: a senha antiga continua nas revisões anteriores.

Ordem:

1. Criar um usuário de banco por serviço, com privilégio mínimo:

```sql
CREATE ROLE api_ordens   LOGIN;  -- leitura e escrita em ordens, idempotencia, outbox
CREATE ROLE api_leitura  LOGIN;  -- SELECT em posicoes, fundos; statement_timeout 3s
CREATE ROLE integrador   LOGIN;  -- UPDATE em ordens (status), INSERT em outbox_webhooks
CREATE ROLE relatorios   LOGIN;  -- SELECT, apontando para a réplica
GRANT SELECT, INSERT, UPDATE ON ordens, idempotencia, outbox TO api_ordens;
GRANT SELECT ON posicoes, fundos, classes TO api_leitura;
ALTER ROLE api_leitura SET statement_timeout = '3s';
```

2. Guardar cada senha no Secrets Manager com rotação automática (função de rotação do RDS, usuário alternado).
3. Na task definition, trocar a variável de ambiente por `secrets`:

```json
{
  "containerDefinitions": [{
    "name": "api-ordens",
    "secrets": [
      { "name": "DB_ESCRITA_PASSWORD", "valueFrom": "arn:aws:secretsmanager:sa-east-1:123456789012:secret:portal/db/api_ordens:password::" },
      { "name": "DB_LEITURA_PASSWORD", "valueFrom": "arn:aws:secretsmanager:sa-east-1:123456789012:secret:portal/db/api_leitura:password::" }
    ]
  }]
}
```

4. Deploy com os usuários novos, conferir que tudo funciona e **só então** trocar a senha do usuário antigo e removê-lo.
5. Revisar o CloudTrail: quem chamou `DescribeTaskDefinition` nos últimos 90 dias.

## IAM de menor privilégio

Execution role (usado pelo ECS para subir a task): ler só os segredos daquele serviço.

```json
{
  "Effect": "Allow",
  "Action": "secretsmanager:GetSecretValue",
  "Resource": "arn:aws:secretsmanager:sa-east-1:123456789012:secret:portal/db/api_ordens-*"
}
```

Task role do integrador: só a própria fila, só as ações que usa.

```json
{
  "Effect": "Allow",
  "Action": ["sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes"],
  "Resource": "arn:aws:sqs:sa-east-1:123456789012:boletas-pendentes"
}
```

Task role da API: `sqs:SendMessage` na fila e nada mais. Nenhum serviço com `sqs:*`, `s3:*` ou `AdministratorAccess`. Revisar com o IAM Access Analyzer (sugestão de política a partir do uso real dos últimos 90 dias).

## Dados de investidores em log

1. **Parar de gravar:** mascaramento no logger, não em cada chamada de log.

.NET (Serilog), destruturação que remove campos sensíveis de qualquer objeto logado:

```csharp
Log.Logger = new LoggerConfiguration()
    .Destructure.ByTransforming<CriarOrdemRequest>(r => new { r.FundoId, r.Tipo, CotistaId = "***" })
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateLogger();
```

Node (pino):

```ts
const log = pino({ redact: { paths: ["*.cpf", "*.nome", "*.email", "*.conta", "body.cotista"], censor: "***" } });
```

Regra de revisão: nenhum `LogInformation("{@Request}", req)` com o objeto inteiro.

2. **O que já foi gravado:** reduzir a retenção dos grupos de log afetados (por exemplo para 30 dias) e levar ao DPO a decisão sobre apagar o histórico e sobre a necessidade de comunicação, conforme a LGPD.
3. **Prevenir regressão:** filtro de métrica no CloudWatch procurando padrão de CPF (`\d{3}\.\d{3}\.\d{3}-\d{2}`) nos grupos de log, com alarme.
