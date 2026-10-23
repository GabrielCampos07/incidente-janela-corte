import { AmbiguousError, PermanentError, type Ordem, type ProcessamentoPort } from "./types.js";

export function createProcessamentoClient(baseUrl: string, timeoutMs: number): ProcessamentoPort {
  return {
    async enviar(ordem, { idempotencyKey }) {
      let res: Response;
      try {
        res = await fetch(`${baseUrl}/ordens`, {
          method: "POST",
          headers: { "content-type": "application/json", "idempotency-key": idempotencyKey },
          body: JSON.stringify(toPayload(ordem)),
          signal: AbortSignal.timeout(timeoutMs),
        });
      } catch (err) {
        // Conexão recusada ou DNS: a requisição não saiu, pode repetir.
        // Timeout ou conexão cortada: pode ter chegado, não pode repetir sem consultar.
        if (isConnectionRefused(err)) throw err;
        throw new AmbiguousError(`sem resposta do processamento: ${String(err)}`);
      }

      if (res.ok) return (await res.json()) as { protocolo: string };
      if (res.status === 408 || res.status === 429) throw new Error(`processamento respondeu ${res.status}`);
      if (res.status >= 400 && res.status < 500) throw new PermanentError(`processamento rejeitou: ${res.status} ${await res.text()}`);
      throw new AmbiguousError(`processamento respondeu ${res.status}`);
    },

    async consultar(ordemId) {
      const res = await fetch(`${baseUrl}/ordens/por-id-externo/${ordemId}`, { signal: AbortSignal.timeout(timeoutMs) });
      if (res.status === 404) return null;
      if (!res.ok) throw new Error(`consulta ao processamento falhou: ${res.status}`);
      return (await res.json()) as { protocolo: string };
    },
  };
}

function toPayload(ordem: Ordem) {
  return {
    idExterno: ordem.id,
    fundo: ordem.fundoId,
    tipo: ordem.tipo,
    valor: ordem.valor,
    dataDeProcessamento: ordem.dataDeProcessamento,
  };
}

function isConnectionRefused(err: unknown): boolean {
  const code = (err as { cause?: { code?: string } })?.cause?.code;
  return code === "ECONNREFUSED" || code === "ENOTFOUND" || code === "EAI_AGAIN";
}
