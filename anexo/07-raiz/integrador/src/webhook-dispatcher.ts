import { createHmac } from "node:crypto";
import type { Pool } from "pg";
import type { Logger } from "./types.js";

// Processo separado do consumidor: um distribuidor com endpoint lento não atrasa o envio de ordens.
// Entrega pelo menos uma vez; o distribuidor descarta repetidos pelo X-Event-Id, que não muda entre tentativas.

export const MAX_TENTATIVAS = 15;
const LEASE_SECONDS = 60;
const TIMEOUT_MS = 10_000;

export interface DestinoWebhook {
  url: string;
  segredo: string;
}

export interface WebhookDeps {
  pool: Pool;
  destinoDe(distribuidorId: string): Promise<DestinoWebhook | null>;
  log: Logger;
  agora?: () => Date;
}

interface Evento {
  event_id: string;
  distribuidor_id: string;
  tipo: string;
  payload: unknown;
  tentativas: number;
}

export function assinar(segredo: string, corpo: string, timestampSeconds: number): string {
  const v1 = createHmac("sha256", segredo).update(`${timestampSeconds}.${corpo}`).digest("hex");
  return `t=${timestampSeconds},v1=${v1}`;
}

/** 30 s, 1 min, 2 min, ... até 6 h. 15 tentativas cobrem cerca de um dia. */
export function proximaTentativaEmSegundos(tentativas: number): number {
  return Math.min(30 * 2 ** tentativas, 6 * 60 * 60);
}

export async function despacharLote(deps: WebhookDeps, tamanho = 20): Promise<number> {
  const { pool } = deps;

  // Reserva com lease em vez de segurar a transação aberta durante as chamadas HTTP.
  const { rows } = await pool.query<Evento>(
    `UPDATE outbox_webhooks SET proximo_envio_em = now() + make_interval(secs => $2)
      WHERE event_id IN (
        SELECT event_id FROM outbox_webhooks
         WHERE entregue_em IS NULL AND falhou_definitivamente = false AND proximo_envio_em <= now()
         ORDER BY proximo_envio_em
         LIMIT $1
         FOR UPDATE SKIP LOCKED)
     RETURNING event_id, distribuidor_id, tipo, payload, tentativas`,
    [tamanho, LEASE_SECONDS],
  );

  await Promise.all(rows.map((evento) => entregar(deps, evento)));
  return rows.length;
}

async function entregar(deps: WebhookDeps, evento: Evento): Promise<void> {
  const { pool, log } = deps;
  const ctx = { eventId: evento.event_id, distribuidorId: evento.distribuidor_id, tentativa: evento.tentativas + 1 };

  const destino = await deps.destinoDe(evento.distribuidor_id);
  if (!destino) {
    await registrarFalha(pool, evento, "distribuidor sem webhook configurado", true);
    return;
  }

  const corpo = JSON.stringify({ id: evento.event_id, tipo: evento.tipo, dados: evento.payload });
  const timestamp = Math.floor((deps.agora?.() ?? new Date()).getTime() / 1000);

  try {
    const res = await fetch(destino.url, {
      method: "POST",
      redirect: "error",
      headers: {
        "content-type": "application/json",
        "x-event-id": evento.event_id,
        "x-portal-signature": assinar(destino.segredo, corpo, timestamp),
      },
      body: corpo,
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });

    if (res.ok) {
      await pool.query(`UPDATE outbox_webhooks SET entregue_em = now(), tentativas = tentativas + 1 WHERE event_id = $1`, [evento.event_id]);
      return;
    }
    const definitivo = res.status >= 400 && res.status < 500 && res.status !== 408 && res.status !== 429;
    await registrarFalha(pool, evento, `HTTP ${res.status}`, definitivo);
    log.warn({ ...ctx, status: res.status }, "webhook recusado");
  } catch (err) {
    await registrarFalha(pool, evento, String(err), false);
    log.warn({ ...ctx, err: String(err) }, "webhook sem resposta");
  }
}

async function registrarFalha(pool: Pool, evento: Evento, erro: string, definitivo: boolean): Promise<void> {
  const tentativas = evento.tentativas + 1;
  const desistir = definitivo || tentativas >= MAX_TENTATIVAS;
  await pool.query(
    `UPDATE outbox_webhooks
        SET tentativas = $2, ultimo_erro = $3, falhou_definitivamente = $4,
            proximo_envio_em = now() + make_interval(secs => $5)
      WHERE event_id = $1`,
    [evento.event_id, tentativas, erro, desistir, proximaTentativaEmSegundos(tentativas)],
  );
}
