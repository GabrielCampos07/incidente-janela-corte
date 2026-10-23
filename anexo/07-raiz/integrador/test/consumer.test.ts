import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { backoffSeconds, createConsumer, type ConsumerConfig } from "../src/consumer.js";
import { AmbiguousError, PermanentError } from "../src/types.js";
import { FakeProcessamento, FakeRepo, FakeSqs, mensagem, silentLog, sleep } from "./fakes.js";

const cfg: ConsumerConfig = {
  workerId: "teste",
  concurrency: 5,
  waitSeconds: 0,
  heartbeatMs: 20,
  visibilityExtensionSeconds: 120,
  leaseSeconds: 180,
  baseBackoffSeconds: 5,
  maxBackoffSeconds: 900,
};

function montar() {
  const sqs = new FakeSqs();
  const repo = new FakeRepo();
  const processamento = new FakeProcessamento();
  const novoConsumer = () => createConsumer({ sqs, repo, processamento, log: silentLog }, cfg);
  return { sqs, repo, processamento, novoConsumer };
}

async function rodar(consumer: ReturnType<ReturnType<typeof montar>["novoConsumer"]>, ms: number) {
  consumer.start();
  await sleep(ms);
  await consumer.stop(1_000);
}

describe("consumidor de boletas-pendentes", () => {
  it("mesma mensagem entregue duas vezes ao mesmo tempo gera um único envio e um único webhook", async () => {
    const { sqs, repo, processamento, novoConsumer } = montar();
    repo.criar("o1");
    processamento.comportamento = () => sleep(60);
    sqs.enfileirar(mensagem("o1", "h1"), mensagem("o1", "h2"));

    await rodar(novoConsumer(), 150);

    assert.deepEqual(processamento.enviados, ["o1"]);
    assert.ok(sqs.deleted.includes("h1"));
    assert.ok(!sqs.deleted.includes("h2"), "a cópia em andamento não é apagada às cegas");
    assert.deepEqual(repo.webhooksEnfileirados, ["o1"]);

    // A cópia volta depois que a visibilidade expira: a ordem já está finalizada, então só apaga.
    sqs.enfileirar(mensagem("o1", "h3", 2));
    await rodar(novoConsumer(), 50);

    assert.deepEqual(processamento.enviados, ["o1"]);
    assert.ok(sqs.deleted.includes("h3"));
    assert.deepEqual(repo.webhooksEnfileirados, ["o1"]);
  });

  it("timeout depois de enviar: a nova tentativa consulta o destino e não reenvia", async () => {
    const { sqs, repo, processamento, novoConsumer } = montar();
    repo.criar("o2");
    processamento.comportamento = async (id) => {
      processamento.noDestino.set(id, "P-chegou");
      throw new AmbiguousError("timeout de 60 s");
    };
    sqs.enfileirar(mensagem("o2", "h1"));
    await rodar(novoConsumer(), 50);

    assert.equal(repo.status.get("o2"), "INCERTA");
    assert.ok(!sqs.deleted.includes("h1"));
    assert.ok(sqs.visibilityChanges.some((v) => v.handle === "h1" && v.seconds >= 5 && v.seconds < 120));

    processamento.comportamento = async () => {};
    sqs.enfileirar(mensagem("o2", "h2", 2));
    await rodar(novoConsumer(), 50);

    assert.equal(processamento.enviados.length, 1, "não reenviou");
    assert.deepEqual(processamento.consultas, ["o2"]);
    assert.equal(repo.status.get("o2"), "ENVIADA");
    assert.equal(repo.protocolos.get("o2"), "P-chegou");
    assert.ok(sqs.deleted.includes("h2"));
  });

  it("timeout sem a ordem no destino: reenvia uma vez", async () => {
    const { sqs, repo, processamento, novoConsumer } = montar();
    repo.criar("o3", "INCERTA");
    sqs.enfileirar(mensagem("o3", "h1", 2));

    await rodar(novoConsumer(), 50);

    assert.deepEqual(processamento.consultas, ["o3"]);
    assert.deepEqual(processamento.enviados, ["o3"]);
    assert.equal(repo.status.get("o3"), "ENVIADA");
  });

  it("rejeição do destino marca a ordem e apaga a mensagem", async () => {
    const { sqs, repo, processamento, novoConsumer } = montar();
    repo.criar("o4");
    processamento.comportamento = async () => { throw new PermanentError("fundo fechado para aplicação"); };
    sqs.enfileirar(mensagem("o4", "h1"));

    await rodar(novoConsumer(), 50);

    assert.equal(repo.status.get("o4"), "REJEITADA");
    assert.ok(sqs.deleted.includes("h1"));
  });

  it("erro transitório libera a ordem e devolve a mensagem com backoff", async () => {
    const { sqs, repo, processamento, novoConsumer } = montar();
    repo.criar("o5");
    processamento.comportamento = async () => { throw new Error("ECONNREFUSED"); };
    sqs.enfileirar(mensagem("o5", "h1", 3));

    await rodar(novoConsumer(), 50);

    assert.equal(repo.status.get("o5"), "ERRO_TRANSITORIO");
    assert.ok(!sqs.deleted.includes("h1"));
    const backoff = sqs.visibilityChanges.find((v) => v.handle === "h1");
    assert.ok(backoff && backoff.seconds >= 20 && backoff.seconds <= 24, `backoff ${backoff?.seconds}`);
  });

  it("processamento mais longo que o heartbeat estende a visibilidade", async () => {
    const { sqs, repo, processamento, novoConsumer } = montar();
    repo.criar("o6");
    processamento.comportamento = () => sleep(90);
    sqs.enfileirar(mensagem("o6", "h1"));

    await rodar(novoConsumer(), 150);

    const extensoes = sqs.visibilityChanges.filter((v) => v.handle === "h1" && v.seconds === 120);
    assert.ok(extensoes.length >= 2, `extensões: ${extensoes.length}`);
    assert.ok(sqs.deleted.includes("h1"));
  });

  it("stop espera a mensagem em andamento terminar e para de buscar novas", async () => {
    const { sqs, repo, processamento, novoConsumer } = montar();
    repo.criar("o7");
    processamento.comportamento = () => sleep(120);
    sqs.enfileirar(mensagem("o7", "h1"));

    const consumer = novoConsumer();
    consumer.start();
    await sleep(20);
    assert.equal(consumer.inflightCount, 1);

    await consumer.stop(1_000);
    const receivesNoStop = sqs.receiveCalls;

    assert.deepEqual(processamento.enviados, ["o7"]);
    assert.equal(repo.status.get("o7"), "ENVIADA");
    assert.ok(sqs.deleted.includes("h1"));
    await sleep(30);
    assert.equal(sqs.receiveCalls, receivesNoStop);
  });

  it("mensagem sem ordemId não é apagada: segue para a DLQ pelo maxReceiveCount", async () => {
    const { sqs, novoConsumer } = montar();
    sqs.enfileirar({ messageId: "x", receiptHandle: "h1", body: "lixo", receiveCount: 1 });

    await rodar(novoConsumer(), 30);

    assert.ok(!sqs.deleted.includes("h1"));
    assert.ok(sqs.visibilityChanges.some((v) => v.handle === "h1" && v.seconds === 900));
  });
});

describe("backoffSeconds", () => {
  it("cresce exponencialmente com jitter de até 20% e respeita o teto", () => {
    for (let i = 0; i < 100; i++) {
      const b1 = backoffSeconds(1, cfg);
      assert.ok(b1 >= 5 && b1 <= 6, `b1=${b1}`);
      const b4 = backoffSeconds(4, cfg);
      assert.ok(b4 >= 40 && b4 <= 48, `b4=${b4}`);
      assert.equal(backoffSeconds(30, cfg), 900);
    }
  });
});
