import type { Logger, Ordem, OrdemRepo, ProcessamentoPort, Reserva, SqsMessage, SqsPort, StatusOrdem } from "../src/types.js";

export const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

export const silentLog: Logger = { info: () => {}, warn: () => {}, error: () => {} };

export function mensagem(ordemId: string, receiptHandle: string, receiveCount = 1): SqsMessage {
  return { messageId: `m-${receiptHandle}`, receiptHandle, body: JSON.stringify({ ordemId }), receiveCount };
}

export class FakeSqs implements SqsPort {
  private lotes: SqsMessage[][] = [];
  deleted: string[] = [];
  visibilityChanges: Array<{ handle: string; seconds: number }> = [];
  receiveCalls = 0;

  enfileirar(...lote: SqsMessage[]) {
    this.lotes.push(lote);
  }

  async receive(): Promise<SqsMessage[]> {
    this.receiveCalls++;
    await sleep(2);
    return this.lotes.shift() ?? [];
  }

  async delete(handle: string) {
    this.deleted.push(handle);
  }

  async changeVisibility(handle: string, seconds: number) {
    this.visibilityChanges.push({ handle, seconds });
  }
}

/** Reproduz a semântica da reserva do repo-pg em memória. */
export class FakeRepo implements OrdemRepo {
  status = new Map<string, StatusOrdem>();
  protocolos = new Map<string, string>();
  webhooksEnfileirados: string[] = [];

  criar(ordemId: string, status: StatusOrdem = "PENDENTE") {
    this.status.set(ordemId, status);
  }

  async reservar(ordemId: string): Promise<Reserva | null> {
    const atual = this.status.get(ordemId);
    if (!atual || !["PENDENTE", "ERRO_TRANSITORIO", "INCERTA"].includes(atual)) return null;
    this.status.set(ordemId, "ENVIANDO");
    return { ordem: ordemDeTeste(ordemId), statusAnterior: atual };
  }

  async estaFinalizada(ordemId: string) {
    return ["ENVIADA", "CONFIRMADA", "REJEITADA"].includes(this.status.get(ordemId) ?? "");
  }

  async marcarEnviada(ordemId: string, protocolo: string) {
    if (this.status.get(ordemId) === "ENVIADA") return;
    this.status.set(ordemId, "ENVIADA");
    this.protocolos.set(ordemId, protocolo);
    this.webhooksEnfileirados.push(ordemId);
  }

  async marcarRejeitada(ordemId: string) {
    this.status.set(ordemId, "REJEITADA");
  }

  async marcarIncerta(ordemId: string) {
    this.status.set(ordemId, "INCERTA");
  }

  async liberar(ordemId: string) {
    this.status.set(ordemId, "ERRO_TRANSITORIO");
  }
}

export class FakeProcessamento implements ProcessamentoPort {
  enviados: string[] = [];
  consultas: string[] = [];
  noDestino = new Map<string, string>();
  comportamento: (ordemId: string) => Promise<void> = async () => {};

  async enviar(ordem: Ordem) {
    this.enviados.push(ordem.id);
    await this.comportamento(ordem.id);
    const protocolo = `P-${ordem.id}`;
    this.noDestino.set(ordem.id, protocolo);
    return { protocolo };
  }

  async consultar(ordemId: string) {
    this.consultas.push(ordemId);
    const protocolo = this.noDestino.get(ordemId);
    return protocolo ? { protocolo } : null;
  }
}

function ordemDeTeste(id: string): Ordem {
  return { id, distribuidorId: "d1", fundoId: "f1", tipo: "APLICACAO", valor: "1000.00", dataDeProcessamento: "2026-10-22" };
}
