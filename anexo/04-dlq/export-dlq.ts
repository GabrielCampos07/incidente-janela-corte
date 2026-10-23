import { SQSClient, ReceiveMessageCommand, GetQueueAttributesCommand, type Message } from "@aws-sdk/client-sqs";
import { S3Client, PutObjectCommand } from "@aws-sdk/client-s3";

// Exporta a DLQ sem apagar nenhuma mensagem. Nunca chama DeleteMessage.
// As mensagens ficam invisíveis durante VISIBILITY_SECONDS e voltam para a DLQ depois.

const DLQ_URL = required("DLQ_URL");
const BUCKET = required("EXPORT_BUCKET");
const KMS_KEY_ID = required("EXPORT_KMS_KEY_ID");
const VISIBILITY_SECONDS = 1800;
const EMPTY_RECEIVES_TO_STOP = 3;

const sqs = new SQSClient({});
const s3 = new S3Client({});

async function main(): Promise<void> {
  const attrs = await sqs.send(new GetQueueAttributesCommand({
    QueueUrl: DLQ_URL,
    AttributeNames: ["ApproximateNumberOfMessages", "MessageRetentionPeriod", "RedrivePolicy"],
  }));
  const expected = Number(attrs.Attributes?.ApproximateNumberOfMessages ?? 0);
  if (attrs.Attributes?.RedrivePolicy) {
    throw new Error("A DLQ tem RedrivePolicy própria: receber mensagens pode movê-las. Abortando.");
  }
  console.log(`esperadas ~${expected} mensagens, retenção ${attrs.Attributes?.MessageRetentionPeriod}s`);

  const seen = new Map<string, object>();
  let emptyStreak = 0;

  while (emptyStreak < EMPTY_RECEIVES_TO_STOP) {
    const { Messages = [] } = await sqs.send(new ReceiveMessageCommand({
      QueueUrl: DLQ_URL,
      MaxNumberOfMessages: 10,
      WaitTimeSeconds: 5,
      VisibilityTimeout: VISIBILITY_SECONDS,
      MessageSystemAttributeNames: ["All"],
      MessageAttributeNames: ["All"],
    }));
    emptyStreak = Messages.length === 0 ? emptyStreak + 1 : 0;
    for (const m of Messages) seen.set(m.MessageId!, toRecord(m));
    if (seen.size % 200 < 10) console.log(`exportadas ${seen.size}`);
  }

  const key = `incidente-2210/dlq-${new Date().toISOString()}.jsonl`;
  await s3.send(new PutObjectCommand({
    Bucket: BUCKET,
    Key: key,
    Body: [...seen.values()].map((r) => JSON.stringify(r)).join("\n"),
    ServerSideEncryption: "aws:kms",
    SSEKMSKeyId: KMS_KEY_ID,
    ContentType: "application/x-ndjson",
  }));

  console.log(`exportadas ${seen.size} de ~${expected} para s3://${BUCKET}/${key}`);
  if (seen.size < expected) {
    console.warn("Menos mensagens que o esperado: rodar de novo depois que a visibilidade expirar e unir por messageId.");
  }
}

function toRecord(m: Message) {
  let body: unknown = m.Body;
  try { body = JSON.parse(m.Body ?? ""); } catch { /* mantém texto bruto */ }
  return {
    messageId: m.MessageId,
    receiveCount: Number(m.Attributes?.ApproximateReceiveCount),
    sentTimestamp: Number(m.Attributes?.SentTimestamp),
    firstReceiveTimestamp: Number(m.Attributes?.ApproximateFirstReceiveTimestamp),
    messageAttributes: m.MessageAttributes,
    body,
  };
}

function required(name: string): string {
  const v = process.env[name];
  if (!v) throw new Error(`variável ${name} não definida`);
  return v;
}

main().catch((err) => { console.error(err); process.exit(1); });
