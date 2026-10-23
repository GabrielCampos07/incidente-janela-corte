# Linha do tempo e teste de hipóteses

Publicar o painel:

```bash
aws cloudwatch put-dashboard --dashboard-name incidente-2210 \
  --dashboard-body file://dashboard.json
```

Os nomes de recursos no JSON (API, ALB, target group, instância RDS) são placeholders. As métricas por rota do API Gateway exigem métricas detalhadas ativadas no estágio; se não estiverem, usar a consulta 1 sobre os access logs.

## Consultas do CloudWatch Logs Insights

Intervalo: 22/10, 13h30 a 15h00 (16h30 a 18h00 UTC).

**1. Requisições por rota por minuto (access logs do API Gateway).** Confirma ou elimina H1 (amplificação pela prévia).

```
fields @timestamp, resourcePath, httpMethod
| stats count() as req by resourcePath, httpMethod, bin(1m)
| sort req desc
```

Rodar também para o mesmo intervalo de 01/10 (antes da prévia) e de cada dia útil desde então. A pergunta é se o p95 cresceu aos poucos ou deu um salto em 22/10.

**2. Latência e status por rota.**

```
fields @timestamp, resourcePath, status, responseLatency, integrationLatency
| filter resourcePath = "/ordens" and httpMethod = "POST"
| stats pct(responseLatency, 95) as p95, count(*) as total by bin(1m)
```

E a distribuição de status no mesmo intervalo:

```
fields @timestamp, status
| filter resourcePath = "/ordens" and httpMethod = "POST"
| stats count(*) as total by status, bin(1m)
```

**3. Erros do Npgsql no app (H2, H3).**

```
fields @timestamp, @message
| filter @message like /connection pool has been exhausted|Timeout during reading attempt|NpgsqlException/
| parse @message /(?<tipo>connection pool has been exhausted|Timeout during reading attempt)/
| stats count() as erros by tipo, bin(1m)
```

"Pool has been exhausted" significa espera por conexão (pool cheio). "Timeout during reading attempt" significa consulta lenta no banco. A proporção entre os dois separa "faltou conexão" de "o banco não respondeu".

**4. Reenvios pelo usuário (mecanismo das 214 duplicadas).**

```
fields @timestamp, principalId, status
| filter resourcePath = "/ordens" and httpMethod = "POST"
| stats count() as tentativas, min(@timestamp) as primeira, max(@timestamp) as ultima by principalId, bin(2m)
| filter tentativas > 3
| sort tentativas desc
```

**5. Integrador: mesma ordem processada mais de uma vez (H8).** Depende do log atual do integrador ter `ordemId` e `messageId`; ajustar os nomes de campo.

```
fields @timestamp, ordemId, messageId, @logStream
| filter @message like /enviando ao processamento/
| stats count() as vezes, count_distinct(@logStream) as tasks by ordemId
| filter vezes > 1
| sort vezes desc
```

**6. Integrador: processamento interrompido no deploy (H11).**

```
fields @timestamp, @logStream, @message
| filter @message like /SIGTERM|exited|enviando ao processamento|enviada com sucesso/
| sort @timestamp asc
```

Procurar logs que mostram "enviando" sem o "enviada com sucesso" correspondente no mesmo `@logStream` antes do fim da task.

## Banco de dados

Performance Insights, intervalo 14h00–14h35: aba "Top SQL" ordenada por carga (AAS) e a divisão por evento de espera. Complementar com:

```sql
-- custo por consulta (acumulado desde o último reset; usar para custo por chamada, não para a janela)
SELECT queryid, calls, round(total_exec_time) AS total_ms, round(mean_exec_time::numeric, 1) AS mean_ms,
       rows, round(rows::numeric / nullif(calls, 0), 0) AS rows_por_chamada, left(query, 140) AS query
FROM pg_stat_statements
ORDER BY total_exec_time DESC
LIMIT 15;

-- conexões presas pela aplicação (H3); rodar durante o pico de hoje, não serve para ontem
SELECT application_name, state, count(*), max(now() - state_change) AS mais_antiga
FROM pg_stat_activity
WHERE datname = current_database()
GROUP BY application_name, state
ORDER BY count(*) DESC;
```

## Matriz de hipóteses (preencher com a evidência)

| # | Hipótese | Evidência que confirma | Evidência que elimina | Resultado |
|---|---|---|---|---|
| H1 | Prévia amplifica a carga | Consulta 1: prévia com várias vezes o volume do POST, e sua consulta no topo do PI | Volume da prévia estável, outra consulta no topo | |
| H2 | Consulta de posições cara | `rows_por_chamada` muito maior que o exibido na tela; espera CPU ou IO:DataFileRead | Custo por chamada baixo | |
| H3 | Conexões presas pela aplicação | Conexões no teto com CPU do RDS baixa; `idle in transaction` | CPU do RDS alta acompanhando as conexões | |
| H4 | Contenção de lock | Espera `Lock:*` dominando no PI | Espera dominante é CPU | |
| H5 | Outro cliente no banco | Carga do PI por usuário/host aponta Lambda ou job | Carga vem só da API | |
| H6 | Cascata de health check | Eventos ECS `unhealthy`, `UnHealthyHostCount > 0`, 502 do ALB | Nenhuma task parada | |
| H7 | Saturação das tasks | CPU ou memória das tasks perto de 100%, OOM | Tasks com folga | |
| H8 | Visibility timeout < processamento | `Received/Sent` bem acima de 1; consulta 5 com ordens repetidas | Razão perto de 1 | |
| H9 | Sistema de processamento lento | Latência de saída do integrador subindo | Latência estável | |
| H10 | Webhooks síncronos prendem o consumidor | Tempo do handler dominado pelo envio de webhooks | Webhooks fora do handler | |
| H11 | Mensagens mortas no deploy | DLQ concentrada após deploy do integrador; consulta 6 | Sem deploy do integrador no período | |
| H12 | Mensagens inválidas | Erros 4xx de validação nas mensagens da DLQ | DLQ sem erro de validação | |
| D | Deploy 13h50 como gatilho | Ver `02-deploy-1350` | Ver `02-deploy-1350` | |

Anomalia a explicar antes de concluir: 40 mil mensagens na fila contra uma média de 22 mil ordens por dia. Conferir qual métrica foi lida (`NumberOfMessagesReceived` conta reentregas) e comparar `NumberOfMessagesSent` do dia com a contagem de ordens criadas no banco no mesmo dia.

```sql
SELECT count(*) FROM ordens
WHERE registrada_em >= '2026-10-22 00:00-03' AND registrada_em < '2026-10-23 00:00-03';
```
