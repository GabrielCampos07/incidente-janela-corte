CREATE TABLE idempotencia (
    distribuidor_id  uuid        NOT NULL,
    chave            uuid        NOT NULL,
    hash_payload     bytea       NOT NULL,
    ordem_id         uuid        NULL REFERENCES ordens(id),
    primeira_chegada timestamptz NOT NULL,
    criado_em        timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (distribuidor_id, chave)
);

CREATE INDEX ix_idempotencia_criado_em ON idempotencia (criado_em);

-- Limpeza diária (job agendado): chaves vivem 7 dias, cobrindo corte, confirmação assíncrona e reconciliação.
-- DELETE FROM idempotencia WHERE criado_em < now() - interval '7 days';

CREATE TABLE outbox (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tipo            text        NOT NULL,
    agregado_id     uuid        NOT NULL,
    correlation_id  text        NOT NULL,
    payload         jsonb       NOT NULL,
    criado_em       timestamptz NOT NULL DEFAULT now(),
    publicada_em    timestamptz NULL,
    tentativas      int         NOT NULL DEFAULT 0
);

CREATE INDEX ix_outbox_pendentes ON outbox (id) WHERE publicada_em IS NULL;
