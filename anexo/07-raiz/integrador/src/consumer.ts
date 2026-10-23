import {
  AmbiguousError,
  PermanentError,
  type Logger,
  type OrdemRepo,
  type ProcessamentoPort,
  type SqsMessage,
  type SqsPort,
} from "./types.js";

export interface ConsumerConfig {
  workerId: string;
  /** Mensagens processadas em paralelo por task. Calibrar pelo rate limit do sistema de processamento. */
  concurrency: number;
  waitSeconds: number;
  heartbeatMs: number;
  visibilityExtensionSeconds: number;
  /** Maior que o timeout da chamada ao processamento, para a reserva não expirar no meio do envio. */
  leaseSeconds: number;
  baseBackoffSeconds: number;
  maxBackoffSeconds: number;
}

export interface ConsumerDeps {
  sqs: SqsPort;
  repo: OrdemRepo;
  processamento: ProcessamentoPort;
  log: Logger;
}

export function createConsumer(deps: ConsumerDeps, cfg: ConsumerConfig) {
  const { sqs, repo, processamento, log } = deps;
  const inflight = new Set<Promise<void>>();
  let stopping = false;
  let loopDone: Promise<void> = Promise.resolve();

  async function loop(): Promise<void> {
    while (!stopping) {
      const capacity = cfg.concurrency - inflight.size;
      if (capacity <= 0) {
        await Promise.race(inflight);
        continue;
      }

      let messages: SqsMessage[];
      try {
        messages = await sqs.receive(Math.min(10, capacity), cfg.waitSeconds);
      } catch (err) {
        log.error({ err }, "falha no ReceiveMessage");
        await sleep(1_000);
        continue;
      }

      for (const msg of messages) {
        if (stopping) {
          await sqs.changeVisibility(msg.receiptHandle, 0).catch(() => undefined);
          continue;
        }
        const task: Promise<void> = handle(msg).finally(() => inflight.delete(task));
        inflight.add(task);
      }
    }
  }

  async function handle(msg: SqsMessage): Promise<void> {
    const ordemId = parseOrdemId(msg);
    if (!ordemId) {
      log.error({ messageId: msg.messageId }, "mensagem sem ordemId; segue para a DLQ pelo maxReceiveCount");
      await sqs.changeVisibility(msg.receiptHandle, cfg.maxBackoffSeconds).catch(() => undefined);
      return;
    }

    const ctx = { ordemId, messageId: msg.messageId, receiveCount: msg.receiveCount };
    const heartbeat = setInterval(() => {
      sqs.changeVisibility(msg.receiptHandle, cfg.visibilityExtensionSeconds)
        .catch((err) => log.warn({ ...ctx, err }, "falha no heartbeat de visibilidade"));
    }, cfg.heartbeatMs);

    try {
      const reserva = await repo.reservar(ordemId, cfg.workerId, cfg.leaseSeconds);
      if (!reserva) {
        if (await repo.estaFinalizada(ordemId)) await sqs.delete(msg.receiptHandle);
        return;
      }

      if (reserva.statusAnterior === "INCERTA") {
        const existente = await processamento.consultar(ordemId);
        if (existente) {
          await repo.marcarEnviada(ordemId, existente.protocolo);
          await sqs.delete(msg.receiptHandle);
          log.info({ ...ctx, protocolo: existente.protocolo }, "ordem incerta já estava no destino");
          return;
        }
      }

      const { protocolo } = await processamento.enviar(reserva.ordem, { idempotencyKey: ordemId });
      await repo.marcarEnviada(ordemId, protocolo);
      await sqs.delete(msg.receiptHandle);
      log.info({ ...ctx, protocolo }, "ordem enviada ao processamento");
    } catch (err) {
      await tratarErro(msg, ordemId, err).catch((err2) =>
        log.error({ ...ctx, err: err2 }, "falha ao tratar erro; a mensagem volta quando a visibilidade expirar"));
    } finally {
      clearInterval(heartbeat);
    }
  }

  async function tratarErro(msg: SqsMessage, ordemId: string, err: unknown): Promise<void> {
    const ctx = { ordemId, messageId: msg.messageId, receiveCount: msg.receiveCount, err };

    if (err instanceof PermanentError) {
      await repo.marcarRejeitada(ordemId, err.message);
      await sqs.delete(msg.receiptHandle);
      log.warn(ctx, "ordem rejeitada pelo destino");
      return;
    }

    if (err instanceof AmbiguousError) {
      await repo.marcarIncerta(ordemId);
      log.warn(ctx, "resultado incerto; a próxima tentativa consulta o destino antes de reenviar");
    } else {
      await repo.liberar(ordemId);
      log.warn(ctx, "erro transitório");
    }
    await sqs.changeVisibility(msg.receiptHandle, backoffSeconds(msg.receiveCount, cfg));
  }

  return {
    start(): void {
      loopDone = loop();
    },
    /** Para de buscar mensagens e espera as que estão em andamento, até timeoutMs. */
    async stop(timeoutMs: number): Promise<void> {
      stopping = true;
      await loopDone;
      await Promise.race([Promise.allSettled([...inflight]), sleep(timeoutMs)]);
      if (inflight.size > 0) log.warn({ pendentes: inflight.size }, "encerrando com mensagens em andamento");
    },
    get inflightCount(): number {
      return inflight.size;
    },
  };
}

export function backoffSeconds(receiveCount: number, cfg: Pick<ConsumerConfig, "baseBackoffSeconds" | "maxBackoffSeconds">): number {
  const exp = Math.min(cfg.baseBackoffSeconds * 2 ** Math.max(receiveCount - 1, 0), cfg.maxBackoffSeconds);
  const jitter = Math.random() * exp * 0.2;
  return Math.min(Math.round(exp + jitter), cfg.maxBackoffSeconds);
}

function parseOrdemId(msg: SqsMessage): string | null {
  try {
    const body = JSON.parse(msg.body) as { ordemId?: unknown };
    return typeof body.ordemId === "string" ? body.ordemId : null;
  } catch {
    return null;
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms).unref());
}
