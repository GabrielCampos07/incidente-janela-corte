import {
  ChangeMessageVisibilityCommand,
  DeleteMessageCommand,
  ReceiveMessageCommand,
  SQSClient,
} from "@aws-sdk/client-sqs";
import type { SqsMessage, SqsPort } from "./types.js";

export function createSqsAdapter(client: SQSClient, queueUrl: string): SqsPort {
  return {
    async receive(max, waitSeconds) {
      const { Messages = [] } = await client.send(new ReceiveMessageCommand({
        QueueUrl: queueUrl,
        MaxNumberOfMessages: max,
        WaitTimeSeconds: waitSeconds,
        MessageSystemAttributeNames: ["ApproximateReceiveCount"],
        MessageAttributeNames: ["correlationId"],
      }));
      return Messages.map((m): SqsMessage => ({
        messageId: m.MessageId!,
        receiptHandle: m.ReceiptHandle!,
        body: m.Body ?? "",
        receiveCount: Number(m.Attributes?.ApproximateReceiveCount ?? 1),
      }));
    },
    async delete(receiptHandle) {
      await client.send(new DeleteMessageCommand({ QueueUrl: queueUrl, ReceiptHandle: receiptHandle }));
    },
    async changeVisibility(receiptHandle, seconds) {
      await client.send(new ChangeMessageVisibilityCommand({
        QueueUrl: queueUrl,
        ReceiptHandle: receiptHandle,
        VisibilityTimeout: seconds,
      }));
    },
  };
}
