# Integrador: consumidor de boletas-pendentes e despachante de webhooks

## O que muda em relação ao atual

| Atual | Novo |
|---|---|
| Visibility timeout 30 s, chamada de até 45 s | Fila com 180 s e heartbeat estendendo para 120 s a cada 30 s |
| Mensagem reaparece e é enviada de novo | Reserva atômica no banco (`reservar`): só um worker envia cada ordem |
| Timeout tratado como erro comum e repetido | Timeout depois de enviar vira `INCERTA`; a próxima tentativa consulta o destino antes de reenviar |
| Webhook disparado dentro do consumidor | Evento na `outbox_webhooks`, na mesma transação do status; despachante separado com `X-Event-Id` estável e assinatura HMAC |
| SIGTERM mata o processamento | Para de buscar, espera as mensagens em andamento até 100 s, encerra |
| Concorrência indefinida | `CONCURRENCY` por task, nunca busca mais mensagens do que consegue processar |

## Arquivos

- `src/consumer.ts`: núcleo da correção (concorrência, heartbeat, classificação de erros, backoff, encerramento).
- `src/repo-pg.ts`: reserva com lease e gravação do status com o evento de webhook na mesma transação.
- `src/processamento-http.ts`: classifica as falhas em permanente, transitória e ambígua.
- `src/webhook-dispatcher.ts`: entrega de webhooks com assinatura, lease e retentativa por evento.
- `test/`: testes com fakes de fila, banco e destino.

```bash
npm install
npm test        # 12 testes
npm run typecheck
```

## Configuração da fila

```bash
aws sqs set-queue-attributes --queue-url $QUEUE_URL --attributes '{
  "VisibilityTimeout": "180",
  "ReceiveMessageWaitTimeSeconds": "20",
  "RedrivePolicy": "{\"deadLetterTargetArn\":\"arn:aws:sqs:sa-east-1:123456789012:boletas-pendentes-dlq\",\"maxReceiveCount\":\"5\"}"
}'
aws sqs set-queue-attributes --queue-url $DLQ_URL --attributes MessageRetentionPeriod=1209600
```

`maxReceiveCount` 5 com backoff de 5 s, 10 s, 20 s, 40 s: uma ordem com erro transitório leva cerca de 1 minuto e meio até a DLQ. Um alarme dispara na primeira mensagem da DLQ (ver `08-proativo`).

## Container e ECS

O sinal precisa chegar ao Node. `npm start` como entrypoint não repassa o SIGTERM.

```dockerfile
FROM node:22-slim
WORKDIR /app
COPY package*.json ./
RUN npm ci --omit=dev
COPY src ./src
USER node
CMD ["node", "--import", "tsx", "src/index.ts"]
```

Na task definition:

```json
{
  "containerDefinitions": [{
    "name": "integrador",
    "stopTimeout": 120,
    "linuxParameters": { "initProcessEnabled": true }
  }]
}
```

No serviço: `minimumHealthyPercent: 100`, `maxPercent: 200`, para a task nova subir antes de a antiga receber o SIGTERM.

## Schema

```sql
ALTER TABLE ordens
    ADD COLUMN reservada_por text,
    ADD COLUMN reserva_ate   timestamptz,
    ADD COLUMN protocolo     text,
    ADD COLUMN motivo_rejeicao text;

CREATE TABLE outbox_webhooks (
    event_id               uuid PRIMARY KEY,
    distribuidor_id        uuid        NOT NULL,
    tipo                   text        NOT NULL,
    payload                jsonb       NOT NULL,
    criado_em              timestamptz NOT NULL DEFAULT now(),
    proximo_envio_em       timestamptz NOT NULL DEFAULT now(),
    tentativas             int         NOT NULL DEFAULT 0,
    entregue_em            timestamptz,
    falhou_definitivamente boolean     NOT NULL DEFAULT false,
    ultimo_erro            text
);

CREATE INDEX ix_outbox_webhooks_pendentes ON outbox_webhooks (proximo_envio_em)
    WHERE entregue_em IS NULL AND falhou_definitivamente = false;
```

## Pendências que dependem de terceiros

- **Idempotência no sistema de processamento:** o cliente envia `idempotency-key` com o ID da ordem. Se o fornecedor não suportar, a proteção fica na reserva e na consulta de ordens incertas, e o endpoint de consulta por ID externo passa a ser obrigatório.
- **Rate limit do processamento:** define `CONCURRENCY`. Começar com 10 por task e ajustar com o fornecedor.
- **Distribuidores:** comunicar o `X-Event-Id` (para descartarem repetidos) e a verificação da assinatura `x-portal-signature`.
- **URL de webhook cadastrada pelo distribuidor:** validar no cadastro (somente HTTPS, sem IPs privados nem `169.254.169.254`) para evitar SSRF. O despachante já recusa redirecionamentos.

## O que decidi não fazer

Migrar para fila FIFO. A deduplicação da FIFO vale só para envios dentro de 5 minutos e não impede reprocessamento por visibility timeout. A idempotência no consumidor é necessária de qualquer jeito.
