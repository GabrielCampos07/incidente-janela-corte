import http from "k6/http";
import { check } from "k6";
import { uuidv4 } from "https://jslib.k6.io/k6-utils/1.4.0/index.js";

// Perfil do pico antes do corte: a prévia multiplica o volume de chamadas enquanto as ordens se concentram.
// Gate de release para mudanças no caminho crítico: rodar em homologação com volume de dados parecido com produção.

const BASE_URL = __ENV.BASE_URL;
const TOKEN = __ENV.TOKEN;
const FUNDO_ID = __ENV.FUNDO_ID;

export const options = {
  scenarios: {
    previa: {
      executor: "ramping-arrival-rate",
      exec: "previa",
      startRate: 20,
      timeUnit: "1s",
      preAllocatedVUs: 100,
      maxVUs: 400,
      stages: [
        { target: 20, duration: "2m" },
        { target: Number(__ENV.PREVIA_PICO_RPS ?? 300), duration: "5m" },
        { target: Number(__ENV.PREVIA_PICO_RPS ?? 300), duration: "10m" },
        { target: 20, duration: "2m" },
      ],
    },
    ordens: {
      executor: "ramping-arrival-rate",
      exec: "criarOrdem",
      startRate: 2,
      timeUnit: "1s",
      preAllocatedVUs: 50,
      maxVUs: 200,
      stages: [
        { target: 2, duration: "2m" },
        { target: Number(__ENV.ORDENS_PICO_RPS ?? 40), duration: "5m" },
        { target: Number(__ENV.ORDENS_PICO_RPS ?? 40), duration: "10m" },
        { target: 2, duration: "2m" },
      ],
    },
  },
  thresholds: {
    "http_req_duration{endpoint:post_ordens}": ["p(95)<300", "p(99)<1000"],
    "http_req_failed{endpoint:post_ordens}": ["rate<0.001"],
    // A prévia pode degradar (429 ou timeout curto) sem reprovar o gate; o envio de ordens não.
    "http_req_duration{endpoint:previa}": ["p(95)<800"],
  },
};

const headers = () => ({
  "content-type": "application/json",
  authorization: `Bearer ${TOKEN}`,
});

export function previa() {
  const linhas = Array.from({ length: 20 }, (_, i) => ({
    linha: i + 1,
    cotistaId: uuidv4(),
    fundoId: FUNDO_ID,
    tipo: "APLICACAO",
    valor: 1000 + i,
  }));
  http.post(`${BASE_URL}/enquadramento/previa`, JSON.stringify({ linhas }), {
    headers: headers(),
    tags: { endpoint: "previa" },
    timeout: "5s",
  });
}

export function criarOrdem() {
  const res = http.post(
    `${BASE_URL}/ordens`,
    JSON.stringify({ fundoId: FUNDO_ID, cotistaId: uuidv4(), tipo: "APLICACAO", valor: 1000 }),
    {
      headers: { ...headers(), "idempotency-key": uuidv4() },
      tags: { endpoint: "post_ordens" },
      timeout: "30s",
    },
  );
  check(res, {
    "201": (r) => r.status === 201,
    "tem recebidaEm": (r) => r.status !== 201 || Boolean(r.json("recebidaEm")),
  });
}
