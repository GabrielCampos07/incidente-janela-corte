import type { Pool } from "pg";
import type { Ordem, OrdemRepo, Reserva, StatusOrdem } from "./types.js";

export function createOrdemRepo(pool: Pool): OrdemRepo {
  return {
    async reservar(ordemId, workerId, leaseSeconds) {
      // Reserva atômica: só um worker por vez envia a ordem. Uma reserva expirada (worker morto) pode ser retomada.
      const { rows } = await pool.query<Ordem & { status_anterior: StatusOrdem }>(
        `WITH antes AS (
           SELECT id, status FROM ordens WHERE id = $1 FOR UPDATE
         )
         UPDATE ordens o
            SET status = 'ENVIANDO', reservada_por = $2, reserva_ate = now() + make_interval(secs => $3)
           FROM antes
          WHERE o.id = antes.id
            AND (antes.status IN ('PENDENTE', 'ERRO_TRANSITORIO', 'INCERTA')
                 OR (antes.status = 'ENVIANDO' AND o.reserva_ate < now()))
         RETURNING o.id, o.distribuidor_id AS "distribuidorId", o.fundo_id AS "fundoId", o.tipo,
                   o.valor::text AS valor, o.data_de_processamento::text AS "dataDeProcessamento",
                   antes.status AS status_anterior`,
        [ordemId, workerId, leaseSeconds],
      );
      if (rows.length === 0) return null;
      const { status_anterior, ...ordem } = rows[0];
      return { ordem, statusAnterior: status_anterior } satisfies Reserva;
    },

    async estaFinalizada(ordemId) {
      const { rows } = await pool.query(
        `SELECT 1 FROM ordens WHERE id = $1 AND status IN ('ENVIADA', 'CONFIRMADA', 'REJEITADA')`,
        [ordemId],
      );
      return rows.length > 0;
    },

    async marcarEnviada(ordemId, protocolo) {
      const client = await pool.connect();
      try {
        await client.query("BEGIN");
        const { rows } = await client.query<{ distribuidor_id: string }>(
          `UPDATE ordens SET status = 'ENVIADA', protocolo = $2, reservada_por = NULL, reserva_ate = NULL
            WHERE id = $1 AND status <> 'ENVIADA'
           RETURNING distribuidor_id`,
          [ordemId, protocolo],
        );
        if (rows.length > 0) {
          await client.query(
            `INSERT INTO outbox_webhooks (event_id, distribuidor_id, tipo, payload)
             VALUES (gen_random_uuid(), $1, 'ordem.enviada', jsonb_build_object('ordemId', $2::uuid, 'protocolo', $3::text))`,
            [rows[0].distribuidor_id, ordemId, protocolo],
          );
        }
        await client.query("COMMIT");
      } catch (err) {
        await client.query("ROLLBACK");
        throw err;
      } finally {
        client.release();
      }
    },

    async marcarRejeitada(ordemId, motivo) {
      await pool.query(
        `UPDATE ordens SET status = 'REJEITADA', motivo_rejeicao = $2, reservada_por = NULL, reserva_ate = NULL WHERE id = $1`,
        [ordemId, motivo],
      );
    },

    async marcarIncerta(ordemId) {
      await pool.query(
        `UPDATE ordens SET status = 'INCERTA', reservada_por = NULL, reserva_ate = NULL WHERE id = $1 AND status = 'ENVIANDO'`,
        [ordemId],
      );
    },

    async liberar(ordemId) {
      await pool.query(
        `UPDATE ordens SET status = 'ERRO_TRANSITORIO', reservada_por = NULL, reserva_ate = NULL WHERE id = $1 AND status = 'ENVIANDO'`,
        [ordemId],
      );
    },
  };
}
