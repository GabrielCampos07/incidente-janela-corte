-- Classificação das ordens afetadas pelo horário de corte em 22/10.
-- Universo: todas as ordens registradas entre 14h30 e 14h45 (não só as 37 reclamadas).
-- Nomes de colunas de produção são suposições: ajustar ao schema real.

CREATE TABLE incidente_2210.tentativas_post (
    request_id      text PRIMARY KEY,
    principal_id    text,          -- identificador do distribuidor no autorizador
    chegada         timestamptz,   -- de requestTimeEpoch
    status          int
);

CREATE TABLE incidente_2210.requisicao_ordem (   -- só se a aplicação logou requestId -> ordemId
    request_id      text PRIMARY KEY,
    ordem_id        uuid
);

CREATE TABLE incidente_2210.reclamadas (         -- lista enviada por Relacionamento
    ordem_id        uuid PRIMARY KEY
);

CREATE TABLE incidente_2210.corte_classificado AS
WITH universo AS (
    SELECT o.id, o.distribuidor_id, d.principal_id, o.registrada_em, o.tipo, o.valor, o.fundo_id
    FROM public.ordens o
    JOIN public.distribuidores d ON d.id = o.distribuidor_id
    WHERE o.registrada_em >= '2026-10-22 14:30:00-03'
      AND o.registrada_em <  '2026-10-22 14:45:00-03'
),
chegada_exata AS (
    SELECT ro.ordem_id, t.chegada
    FROM incidente_2210.requisicao_ordem ro
    JOIN incidente_2210.tentativas_post t ON t.request_id = ro.request_id
),
falhas_antes_do_corte AS (
    SELECT principal_id, count(*) AS falhas, min(chegada) AS primeira_falha
    FROM incidente_2210.tentativas_post
    WHERE chegada < '2026-10-22 14:30:00-03' AND status >= 500
    GROUP BY principal_id
)
SELECT u.id AS ordem_id,
       u.distribuidor_id,
       u.registrada_em,
       ce.chegada                                 AS chegada_da_requisicao,
       f.falhas                                   AS falhas_do_distribuidor_antes_do_corte,
       f.primeira_falha,
       (r.ordem_id IS NOT NULL)                   AS reclamada,
       CASE
           WHEN ce.chegada < '2026-10-22 14:30:00-03'  THEN 'A'
           WHEN f.falhas > 0                           THEN 'B'
           WHEN ce.chegada IS NOT NULL                 THEN 'C'
           ELSE                                             'S'
       END AS classe
FROM universo u
LEFT JOIN chegada_exata ce             ON ce.ordem_id = u.id
LEFT JOIN falhas_antes_do_corte f      ON f.principal_id = u.principal_id
LEFT JOIN incidente_2210.reclamadas r  ON r.ordem_id = u.id;

-- Sem requisicao_ordem, as ordens só caem em B ou S: a revisão manual decide.
-- Quando o distribuidor teve menos falhas antes do corte que ordens depois, nem todas as ordens dele são B.
SELECT distribuidor_id,
       count(*) FILTER (WHERE classe = 'B') AS ordens_b,
       max(falhas_do_distribuidor_antes_do_corte) AS falhas_antes
FROM incidente_2210.corte_classificado
GROUP BY distribuidor_id
HAVING count(*) FILTER (WHERE classe = 'B') > max(falhas_do_distribuidor_antes_do_corte);

-- Resumo: reclamadas e não reclamadas por classe
SELECT classe, reclamada, count(*)
FROM incidente_2210.corte_classificado
GROUP BY classe, reclamada
ORDER BY classe, reclamada;

-- Impacto por ordem das classes A e B (insumo para Operações; cota em cota_fundo(fundo_id, data, valor_cota))
SELECT c.ordem_id, o.tipo, o.valor,
       d0.valor_cota AS cota_d0,
       d1.valor_cota AS cota_d1,
       CASE o.tipo
           WHEN 'APLICACAO' THEN round(o.valor / d0.valor_cota - o.valor / d1.valor_cota, 8)
       END AS diferenca_em_cotas_aplicacao,
       CASE o.tipo
           WHEN 'RESGATE'   THEN round(o.valor / d1.valor_cota - o.valor / d0.valor_cota, 8)
       END AS diferenca_em_cotas_resgatadas
FROM incidente_2210.corte_classificado c
JOIN public.ordens o    ON o.id = c.ordem_id
JOIN public.cota_fundo d0 ON d0.fundo_id = o.fundo_id AND d0.data = date '2026-10-22'
JOIN public.cota_fundo d1 ON d1.fundo_id = o.fundo_id AND d1.data = date '2026-10-23'
WHERE c.classe IN ('A', 'B');
