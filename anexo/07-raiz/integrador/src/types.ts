export interface SqsMessage {
  messageId: string;
  receiptHandle: string;
  body: string;
  receiveCount: number;
}

export interface SqsPort {
  receive(max: number, waitSeconds: number): Promise<SqsMessage[]>;
  delete(receiptHandle: string): Promise<void>;
  changeVisibility(receiptHandle: string, seconds: number): Promise<void>;
}

export type StatusOrdem =
  | "PENDENTE"
  | "ENVIANDO"
  | "INCERTA"
  | "ERRO_TRANSITORIO"
  | "ENVIADA"
  | "REJEITADA"
  | "CONFIRMADA";

export interface Ordem {
  id: string;
  distribuidorId: string;
  fundoId: string;
  tipo: "APLICACAO" | "RESGATE";
  valor: string;
  dataDeProcessamento: string;
}

export interface Reserva {
  ordem: Ordem;
  statusAnterior: StatusOrdem;
}

export interface OrdemRepo {
  reservar(ordemId: string, workerId: string, leaseSeconds: number): Promise<Reserva | null>;
  estaFinalizada(ordemId: string): Promise<boolean>;
  /** Grava o status e o evento de webhook na outbox, na mesma transação. */
  marcarEnviada(ordemId: string, protocolo: string): Promise<void>;
  marcarRejeitada(ordemId: string, motivo: string): Promise<void>;
  marcarIncerta(ordemId: string): Promise<void>;
  liberar(ordemId: string): Promise<void>;
}

export interface ProcessamentoPort {
  enviar(ordem: Ordem, opts: { idempotencyKey: string }): Promise<{ protocolo: string }>;
  consultar(ordemId: string): Promise<{ protocolo: string } | null>;
}

export interface Logger {
  info(obj: object, msg: string): void;
  warn(obj: object, msg: string): void;
  error(obj: object, msg: string): void;
}

/** O destino rejeitou a ordem (validação). Repetir não muda o resultado. */
export class PermanentError extends Error {}

/** A requisição pode ter chegado ao destino (timeout depois de enviar). Não reenviar sem consultar. */
export class AmbiguousError extends Error {}
