import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { describe, it } from "node:test";
import { assinar, MAX_TENTATIVAS, proximaTentativaEmSegundos } from "../src/webhook-dispatcher.js";

describe("assinatura do webhook", () => {
  it("é um HMAC-SHA256 de timestamp.corpo que o distribuidor consegue recalcular", () => {
    const corpo = JSON.stringify({ id: "e1", tipo: "ordem.enviada" });
    const assinatura = assinar("segredo-do-distribuidor", corpo, 1_792_771_200);

    const esperado = createHmac("sha256", "segredo-do-distribuidor").update(`1792771200.${corpo}`).digest("hex");
    assert.equal(assinatura, `t=1792771200,v1=${esperado}`);
  });

  it("muda se o corpo for alterado", () => {
    assert.notEqual(assinar("s", '{"valor":100}', 1), assinar("s", '{"valor":101}', 1));
  });
});

describe("agenda de retentativa do webhook", () => {
  it("começa em 30 s, dobra e para em 6 h", () => {
    assert.equal(proximaTentativaEmSegundos(0), 30);
    assert.equal(proximaTentativaEmSegundos(1), 60);
    assert.equal(proximaTentativaEmSegundos(MAX_TENTATIVAS), 6 * 60 * 60);
  });
});
