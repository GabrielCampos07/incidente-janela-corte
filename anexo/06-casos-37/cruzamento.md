# Os 37 casos: horário de chegada contra horário registrado

Objetivo: para cada ordem, provar quando a requisição chegou, independente do horário gravado pelo portal. E fazer a mesma análise para todas as ordens potencialmente afetadas, não só as que reclamaram.

## Fontes de evidência, em ordem de força

1. **Access logs do API Gateway** (`requestTimeEpoch`): horário de chegada na borda, fora do alcance da aplicação. Só existem se já estavam ativos em 22/10.
2. **Access logs do ALB** no S3 (`request_creation_time`), se habilitados.
3. **Log da aplicação** com o início da requisição. Fraco: sob saturação, a própria aplicação atrasa.

Se nenhuma das três existir para uma ordem, registrar como "sem evidência" e levar a decisão a Compliance; não inferir.

## Passo 1: extrair as tentativas de POST /ordens

Logs Insights sobre os access logs, 22/10 das 14h00 às 14h45, exportado como CSV para `incidente_2210.tentativas_post`:

```
fields requestId, principalId, requestTimeEpoch, status, responseLatency
| filter resourcePath = "/ordens" and httpMethod = "POST"
| sort requestTimeEpoch asc
| limit 10000
```

Alternativa com os logs do ALB via Athena (tabela `alb_logs` criada conforme a documentação da AWS):

```sql
SELECT request_creation_time, time AS resposta, elb_status_code, target_status_code, trace_id, client_ip
FROM alb_logs
WHERE request_verb = 'POST' AND request_url LIKE '%/ordens%'
  AND request_creation_time BETWEEN '2026-10-22T17:00:00' AND '2026-10-22T17:45:00'
ORDER BY request_creation_time;
```

## Passo 2: ligar tentativa e ordem

Se a aplicação loga o `requestId` do API Gateway (ou o `X-Amzn-Trace-Id` do ALB) junto com o ID da ordem criada, a ligação é exata: carregar em `incidente_2210.requisicao_ordem`.

Se não loga, a ligação é por distribuidor e horário, e cada caso ambíguo vai para revisão manual. A ausência desse identificador é um dos achados do incidente (ver `08-proativo`, correlation ID).

## Passo 3: classificar

Rodar `cruzamento.sql`. Cada ordem cai em uma classe:

| Classe | Situação | Recomendação técnica |
|---|---|---|
| A | A requisição que criou a ordem chegou antes das 14h30 | D0. Falha nossa, sem ambiguidade |
| B | Houve tentativa com 5xx antes das 14h30 sem ordem criada, e a ordem foi criada por um reenvio depois | Recomendo D0 (a falha foi nossa), mas é decisão de Compliance |
| C | Nenhuma tentativa antes das 14h30 | Manter D+1 e apresentar a evidência ao distribuidor |
| S | Sem evidência de chegada | Compliance decide |

## Passo 4: impacto financeiro por ordem (insumo para Operações)

Para cada ordem das classes A e B: valor, tipo, cota de D0, cota de D+1 e a diferença. A forma de reparação (reprocessamento com o administrador ou ressarcimento da diferença) não é decisão de engenharia.

## Regras

- Não alterar `registrada_em` nem qualquer dado da ordem. Correções viram registros de ajuste auditáveis (quem, quando, por quê, evidência).
- A planilha entregue a Relacionamento identifica ordens por ID e protocolo, sem CPF nem nome de investidor.
- Uma segunda pessoa confere a classificação antes da entrega.
