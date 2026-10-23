# Consulta de posições, índice, prévia em lote e isolamento de pools

## Arquivos

- `ConsultaPosicoes.cs`: projeção direta para DTO, filtros no banco, `AsNoTracking`, paginação por cursor, `CancellationToken`. Substitui o padrão `Include` + `ToListAsync` + filtro em memória.
- `PreviaEnquadramento.cs`: a grade inteira em uma chamada, com uma consulta agregada (`GROUP BY` + `SUM`) por conjunto de cotistas. A regra é a mesma `RegraDeEnquadramento` usada no envio da ordem.

## Índices

Criar via migration com `migrationBuilder.Sql(..., suppressTransaction: true)`, porque `CREATE INDEX CONCURRENTLY` não roda dentro de transação. Sem `CONCURRENTLY`, a criação bloqueia escrita na tabela.

```sql
-- listagem de posições por distribuidor, com filtro opcional por fundo e cursor por id
CREATE INDEX CONCURRENTLY ix_posicoes_distribuidor_fundo_id
    ON posicoes (distribuidor_id, fundo_id, id)
    INCLUDE (saldo, quantidade_cotas)
    WHERE saldo > 0;

-- prévia: saldos por cotista dentro do distribuidor
CREATE INDEX CONCURRENTLY ix_posicoes_distribuidor_cotista
    ON posicoes (distribuidor_id, cotista_id)
    INCLUDE (fundo_id, saldo)
    WHERE saldo > 0;
```

Se a listagem sem filtro de fundo for a mais comum, o `EXPLAIN` pode mostrar que um índice `(distribuidor_id, id)` serve melhor. A decisão sai do plano de execução com volume de produção, não daqui.

## Verificação antes do merge

```csharp
// no teste de integração ou num endpoint de diagnóstico em homologação
var sql = query.ToQueryString();
```

```sql
EXPLAIN (ANALYZE, BUFFERS) <sql gerado, com os parâmetros de um distribuidor grande>;
```

O que conferir: um único comando, sem JOIN com movimentações, `Index Scan` ou `Index Only Scan` nos índices acima, linhas lidas próximas das linhas retornadas.

## Pools separados para escrita e leitura

O POST /ordens não pode esperar conexão atrás de consultas da prévia. Duas connection strings, dois pools:

```json
{
  "ConnectionStrings": {
    "Escrita": "Host=portal-db;Database=portal;Username=api_ordens;Maximum Pool Size=40;Timeout=5;Command Timeout=15;Application Name=api-ordens-escrita",
    "Leitura": "Host=portal-db-replica;Database=portal;Username=api_leitura;Maximum Pool Size=30;Timeout=2;Command Timeout=5;Application Name=api-leitura"
  }
}
```

Senhas vêm do Secrets Manager (ver `08-proativo`), nunca da connection string em texto.

```csharp
builder.Services.AddDbContextPool<PortalDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Escrita")));
builder.Services.AddDbContextPool<LeituraDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Leitura"))
    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
```

```sql
-- no banco: a prévia nunca segura o banco por mais de 3 s
ALTER ROLE api_leitura SET statement_timeout = '3s';
```

`Timeout=2` no pool de leitura faz a prévia falhar rápido quando o pool estiver cheio, em vez de enfileirar requisições por 15 s. O front trata a falha como "prévia indisponível".

Orçamento de conexões: `tasks x (40 + 30)`. Com 4 tasks, 280 conexões. Conferir contra `max_connections` antes de aumentar o número de tasks ou os pools. Sem réplica de leitura, as duas connection strings apontam para o primário e o isolamento continua valendo (pools separados); só a carga no banco não muda.

## Como provar que funcionou

| Número | Onde | Antes | Depois |
|---|---|---|---|
| Carga (AAS) e tempo médio por chamada da consulta de posições/prévia | Performance Insights, Top SQL, 14h00–14h30 | | |
| Chamadas por segundo da prévia | Contagem por rota no API Gateway | | |
| p95 e p99 do POST /ordens, 14h00–14h30 | CloudWatch, `Latency` do API Gateway por recurso | | |
| Pico de `DatabaseConnections` | CloudWatch, RDS | | |
| Vazão máxima com p95 do POST < 300 ms | k6 em homologação com o perfil do pico | | |
