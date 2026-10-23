import { SQSClient } from "@aws-sdk/client-sqs";
import { hostname } from "node:os";
import pg from "pg";
import { createConsumer } from "./consumer.js";
import { createProcessamentoClient } from "./processamento-http.js";
import { createOrdemRepo } from "./repo-pg.js";
import { createSqsAdapter } from "./sqs-adapter.js";
import type { Logger } from "./types.js";

// ECS manda SIGTERM e espera stopTimeout (120 s na task definition) antes do SIGKILL.
const SHUTDOWN_BUDGET_MS = 100_000;

const log: Logger = {
  info: (obj, msg) => console.log(JSON.stringify({ level: "info", msg, ...obj })),
  warn: (obj, msg) => console.warn(JSON.stringify({ level: "warn", msg, ...obj })),
  error: (obj, msg) => console.error(JSON.stringify({ level: "error", msg, ...obj })),
};

const pool = new pg.Pool({ connectionString: process.env.DATABASE_URL, max: 20 });

const consumer = createConsumer(
  {
    sqs: createSqsAdapter(new SQSClient({}), process.env.QUEUE_URL!),
    repo: createOrdemRepo(pool),
    processamento: createProcessamentoClient(process.env.PROCESSAMENTO_URL!, 60_000),
    log,
  },
  {
    workerId: `${hostname()}-${process.pid}`,
    concurrency: Number(process.env.CONCURRENCY ?? 10),
    waitSeconds: 20,
    heartbeatMs: 30_000,
    visibilityExtensionSeconds: 120,
    leaseSeconds: 180,
    baseBackoffSeconds: 5,
    maxBackoffSeconds: 900,
  },
);

consumer.start();
log.info({}, "integrador iniciado");

let shuttingDown = false;
for (const signal of ["SIGTERM", "SIGINT"] as const) {
  process.on(signal, async () => {
    if (shuttingDown) return;
    shuttingDown = true;
    log.info({ signal, inflight: consumer.inflightCount }, "encerrando: parando de buscar mensagens");
    await consumer.stop(SHUTDOWN_BUDGET_MS);
    await pool.end();
    log.info({}, "encerrado");
    process.exit(0);
  });
}
