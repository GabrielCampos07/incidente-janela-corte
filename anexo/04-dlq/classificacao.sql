-- Classificação das mensagens da DLQ de boletas-pendentes (incidente de 22/10).
-- Rodar num schema de trabalho isolado, com usuário somente leitura nas tabelas de produção.
-- Nomes de tabelas e colunas de produção (ordens, status) são suposições: ajustar ao schema real.

CREATE SCHEMA IF NOT EXISTS incidente_2210;

-- Export da DLQ (gerado por export-dlq.ts e convertido com jq, ver procedimento.md)
CREATE TABLE incidente_2210.dlq_export (
    message_id      text PRIMARY KEY,
    ordem_id        uuid,
    receive_count   int,
    sent_at         timestamptz,
    body            jsonb
);

-- Extrato do sistema de processamento para 22/10 e 23/10: uma linha por ordem recebida por ele
CREATE TABLE incidente_2210.processamento_extrato (
    ordem_id_externo uuid,
    protocolo        text,
    recebida_em      timestamptz,
    situacao         text
);

-- Envios de webhook, extraídos dos logs do integrador
CREATE TABLE incidente_2210.webhook_envios (
    ordem_id        uuid,
    distribuidor_id uuid,
    enviado_em      timestamptz,
    status_http     int
);

CREATE TABLE incidente_2210.dlq_classificada AS
WITH proc AS (
    SELECT ordem_id_externo AS ordem_id,
           count(*)               AS vezes,
           array_agg(protocolo)   AS protocolos
    FROM incidente_2210.processamento_extrato
    GROUP BY ordem_id_externo
),
wh AS (
    SELECT ordem_id,
           count(*) FILTER (WHERE status_http BETWEEN 200 AND 299) AS webhooks_ok,
           count(*)                                                AS webhooks_total
    FROM incidente_2210.webhook_envios
    GROUP BY ordem_id
),
msgs AS (
    SELECT ordem_id,
           count(*)           AS mensagens_na_dlq,
           max(receive_count) AS max_receive_count,
           min(sent_at)       AS primeira_publicacao
    FROM incidente_2210.dlq_export
    GROUP BY ordem_id
)
SELECT m.ordem_id,
       m.mensagens_na_dlq,
       m.max_receive_count,
       m.primeira_publicacao,
       o.status                    AS status_no_portal,
       coalesce(p.vezes, 0)        AS vezes_no_processamento,
       p.protocolos,
       coalesce(w.webhooks_ok, 0)  AS webhooks_ok,
       CASE
           WHEN o.id IS NULL                       THEN 'ORFA_SEM_ORDEM'
           WHEN coalesce(p.vezes, 0) = 0           THEN 'NAO_PROCESSADA'
           WHEN p.vezes > 1                        THEN 'DUPLICADA_NO_DESTINO'
           WHEN o.status NOT IN ('ENVIADA', 'CONFIRMADA')
                                                   THEN 'PROCESSADA_STATUS_DESATUALIZADO'
           WHEN coalesce(w.webhooks_ok, 0) = 0     THEN 'PROCESSADA_SEM_WEBHOOK'
           ELSE                                         'PROCESSADA_COMPLETA'
       END AS classe
FROM msgs m
LEFT JOIN public.ordens o ON o.id = m.ordem_id
LEFT JOIN proc p          ON p.ordem_id = m.ordem_id
LEFT JOIN wh w            ON w.ordem_id = m.ordem_id;

-- Resumo para o canal do incidente
SELECT classe, count(*) AS ordens, sum(mensagens_na_dlq) AS mensagens
FROM incidente_2210.dlq_classificada
GROUP BY classe
ORDER BY ordens DESC;

-- Relação com o deploy (H11) e com a reentrega (H8):
-- concentração logo após o deploy do integrador indica processamento interrompido;
-- max_receive_count igual ao maxReceiveCount com vezes_no_processamento >= 1 indica reentrega de mensagem já processada.
SELECT date_trunc('minute', primeira_publicacao AT TIME ZONE 'America/Sao_Paulo') AS minuto,
       classe, count(*)
FROM incidente_2210.dlq_classificada
GROUP BY 1, 2
ORDER BY 1;
