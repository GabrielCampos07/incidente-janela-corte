# DLQ de boletas-pendentes: 1.900 mensagens

Regra até a reconciliação terminar: **nada de redrive, nada de purge, nada de DeleteMessage.**

## 1. Retenção (fazer primeiro)

No SQS padrão, a expiração conta a partir do envio original da mensagem, não da chegada na DLQ. Com retenção padrão de 4 dias, mensagens de 22/10 podem expirar a partir de 26/10.

```bash
aws sqs get-queue-attributes --queue-url $QUEUE_URL --attribute-names MessageRetentionPeriod
aws sqs get-queue-attributes --queue-url $DLQ_URL   --attribute-names MessageRetentionPeriod RedrivePolicy

aws sqs set-queue-attributes --queue-url $DLQ_URL --attributes MessageRetentionPeriod=1209600
```

O aumento para 14 dias é uma proteção a mais; a garantia é o export do passo 2.

## 2. Export para o S3

Bucket: acesso restrito ao time do incidente, criptografia KMS, bloqueio de acesso público, regra de ciclo de vida apagando em 90 dias. O corpo das mensagens tem dados de ordens e investidores.

```bash
npm install
DLQ_URL=$DLQ_URL EXPORT_BUCKET=portal-incidentes EXPORT_KMS_KEY_ID=alias/portal-incidentes \
  npx tsx export-dlq.ts
```

O script recusa rodar se a DLQ tiver `RedrivePolicy` própria. As mensagens ficam invisíveis por 30 minutos e voltam sozinhas.

## 3. Carga no schema de trabalho

```bash
aws s3 cp s3://portal-incidentes/incidente-2210/dlq-<data>.jsonl dlq.jsonl

jq -r '[.messageId, .body.ordemId, .receiveCount, (.sentTimestamp/1000 | todate), (.body | tojson)] | @csv' \
  dlq.jsonl > dlq.csv

psql "$DATABASE_URL_LEITURA" -c "\copy incidente_2210.dlq_export FROM 'dlq.csv' CSV"
```

Pedir à equipe do sistema de processamento o extrato de 22/10 e 23/10 por ID externo da ordem e carregar em `processamento_extrato`. Extrair os envios de webhook dos logs do integrador para `webhook_envios`.

Apagar `dlq.jsonl` e `dlq.csv` da máquina local ao final.

## 4. Classificação e ação por classe

Rodar `classificacao.sql`.

| Classe | Significado | Ação | Quando |
|---|---|---|---|
| `DUPLICADA_NO_DESTINO` | Chegou duas ou mais vezes ao processamento | Lista para Operações cancelar a duplicata (ver `05-duplicatas`) | **Hoje, antes do cálculo da cota** |
| `NAO_PROCESSADA` | Nunca chegou ao processamento | Reenviar pelo consumidor corrigido, em lotes de 50, conferindo o extrato a cada lote. Ordem de 22/10 que deveria ter entrado em D0 entra também na análise de `06-casos-37` | Depois da janela |
| `PROCESSADA_STATUS_DESATUALIZADO` | Processada, mas o portal não sabe | Atualizar status no portal com registro de auditoria | Depois da janela |
| `PROCESSADA_SEM_WEBHOOK` | Processada, distribuidor não foi avisado | Enviar webhook com `X-Event-Id` estável | Depois da janela |
| `PROCESSADA_COMPLETA` | Tudo certo, foi para a DLQ só por reentrega | Nenhuma | Pode sair da DLQ após aprovação |
| `ORFA_SEM_ORDEM` | Mensagem sem ordem no banco (publicação sem commit) | Investigar individualmente; não reenviar | Depois da janela |

Só depois de todas as classes tratadas e conferidas por uma segunda pessoa, apagar da DLQ as mensagens resolvidas.

## 5. O que reportar

- Quantidade por classe e por minuto de publicação.
- Quantas mensagens foram para a DLQ depois de já processadas (reentrega, H8) e quantas por interrupção no deploy (H11).
- As 214 ordens duplicadas pela API passam pela mesma matriz, pelo ID da ordem.
