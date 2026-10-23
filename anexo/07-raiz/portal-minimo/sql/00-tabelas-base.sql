-- Tabelas que o anexo assume que já existem no portal. Rodam antes do ../../idempotencia/schema.sql
-- (que tem FK pra ordens). Valores e nomes são suposição, como o resto do anexo.

CREATE TABLE ordens (
    id                     uuid          PRIMARY KEY,
    distribuidor_id        uuid          NOT NULL,
    fundo_id               uuid          NOT NULL,
    cotista_id             uuid          NOT NULL,
    tipo                   text          NOT NULL,
    valor                  numeric(18,2) NOT NULL CHECK (valor > 0),
    status                 text          NOT NULL,
    recebida_em            timestamptz   NOT NULL,   -- chegada no API Gateway, imutável
    data_de_processamento  date          NOT NULL,
    registrada_em          timestamptz   NOT NULL DEFAULT now()
);

CREATE INDEX ix_ordens_cotista ON ordens (cotista_id);

CREATE TABLE classes (
    id      uuid PRIMARY KEY,
    codigo  text NOT NULL
);

CREATE TABLE fundos (
    id         uuid PRIMARY KEY,
    nome       text NOT NULL,
    ativo      boolean NOT NULL DEFAULT true,
    classe_id  uuid NOT NULL REFERENCES classes(id)
);

CREATE TABLE posicoes (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    distribuidor_id   uuid          NOT NULL,
    fundo_id          uuid          NOT NULL REFERENCES fundos(id),
    cotista_id        uuid          NOT NULL,
    quantidade_cotas  numeric(28,8) NOT NULL,
    saldo             numeric(18,2) NOT NULL,
    data_referencia   date          NOT NULL
);

-- índice da listagem de posições (o mesmo de ../../consulta-posicoes/README.md, sem CONCURRENTLY porque a tabela nasce vazia)
CREATE INDEX ix_posicoes_distribuidor_fundo_id
    ON posicoes (distribuidor_id, fundo_id, id)
    INCLUDE (saldo, quantidade_cotas)
    WHERE saldo > 0;
