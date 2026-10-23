# Correlation ID e tracing

Prioridade 3. Hoje não dá para responder "o que aconteceu com a ordem X". Foi por isso que a DLQ virou um mistério e que os 37 casos dependem de cruzamento por horário.

## Um identificador do Gateway até o webhook

| Ponto | De onde vem | Para onde vai |
|---|---|---|
| API Gateway | `$context.requestId` | Header `X-Correlation-Id` na integração e no access log |
| API .NET | Header `X-Correlation-Id` (ou novo, se ausente) | Todos os logs da requisição, coluna `correlation_id` da outbox, header da resposta |
| SQS | Atributo de mensagem `correlationId` (já enviado pelo `OutboxPublisher`) | Integrador |
| Integrador | Atributo da mensagem | Logs, header da chamada ao processamento, `outbox_webhooks` |
| Webhook | `outbox_webhooks` | Corpo do evento |

Com isso, o access log (`requestId`, horário de chegada) se liga à ordem criada sem heurística.

## API .NET

```csharp
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-Id"].FirstOrDefault() ?? context.TraceIdentifier;
    context.TraceIdentifier = correlationId;
    context.Response.Headers["X-Correlation-Id"] = correlationId;

    using (LogContext.PushProperty("correlationId", correlationId))
        await next();
});
```

## Integrador

```ts
const correlationId = msg.messageAttributes?.correlationId ?? msg.messageId;
const log = baseLog.child({ correlationId, ordemId });
```

## Formato mínimo dos logs (JSON)

`timestamp`, `level`, `service`, `correlationId`, `ordemId`, `distribuidorId`, `evento`, `duracaoMs`. Nunca CPF, nome, conta ou valor associado a investidor identificável.

## Tracing distribuído

OpenTelemetry nos dois serviços (instrumentação automática de ASP.NET Core, Npgsql, HttpClient, AWS SDK e `pg` no Node), exportando para o X-Ray via ADOT collector como sidecar. O SQS não propaga o contexto de trace sozinho: o `traceparent` vai como atributo de mensagem, ao lado do `correlationId`.

Começar com amostragem de 5% e 100% das requisições com erro ou acima de 1 s, para controlar custo.
