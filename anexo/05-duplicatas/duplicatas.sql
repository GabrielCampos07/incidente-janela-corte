-- Duplicatas do incidente de 22/10, em dois tipos que exigem tratamentos diferentes.
-- Nomes de colunas de produção são suposições: ajustar ao schema real.

-- Tipo A: a mesma ordem (mesmo ID) chegou mais de uma vez ao sistema de processamento.
-- Origem: reentrega no integrador. A ordem é uma só; a cópia no destino precisa ser cancelada.
SELECT p.ordem_id_externo        AS ordem_id,
       o.distribuidor_id,
       o.fundo_id,
       o.tipo,                       -- aplicação ou resgate
       o.valor,
       count(*)                  AS vezes_no_destino,
       array_agg(p.protocolo ORDER BY p.recebida_em) AS protocolos,
       min(p.recebida_em)        AS primeira,
       max(p.recebida_em)        AS ultima
FROM incidente_2210.processamento_extrato p
JOIN public.ordens o ON o.id = p.ordem_id_externo
WHERE p.recebida_em >= '2026-10-22 13:30-03'
GROUP BY 1, 2, 3, 4, 5
HAVING count(*) > 1;

-- Tipo B: ordens diferentes (IDs diferentes) com o mesmo conteúdo, criadas em sequência pela API.
-- Origem: cliques repetidos sem chave de idempotência. Pode haver ordem legítima repetida:
-- por isso é "candidata" e a confirmação é do distribuidor, não nossa.
WITH candidatas AS (
    SELECT o.*,
           lag(o.id)            OVER w AS ordem_anterior,
           lag(o.registrada_em) OVER w AS registrada_anterior
    FROM public.ordens o
    WHERE o.registrada_em >= '2026-10-22 13:30-03'
      AND o.registrada_em <  '2026-10-22 15:30-03'
    WINDOW w AS (PARTITION BY o.distribuidor_id, o.cotista_id, o.fundo_id, o.tipo, o.valor
                 ORDER BY o.registrada_em)
)
SELECT distribuidor_id, cotista_id, fundo_id, tipo, valor,
       ordem_anterior AS ordem_original,
       id             AS ordem_candidata_duplicada,
       registrada_anterior, registrada_em,
       registrada_em - registrada_anterior AS intervalo
FROM candidatas
WHERE ordem_anterior IS NOT NULL
  AND registrada_em - registrada_anterior < interval '5 minutes'
ORDER BY distribuidor_id, registrada_em;
