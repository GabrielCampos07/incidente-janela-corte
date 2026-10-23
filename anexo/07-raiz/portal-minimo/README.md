# Portal mínimo: pra rodar o C# do anexo de verdade

Os arquivos de `corte`, `idempotencia` e `consulta-posicoes` dependem de tipos do portal que eu não tenho (`PortalDbContext`, `Ordem`, `RegraDeEnquadramento` e companhia). Montei aqui o mínimo pra eles compilarem e os testes rodarem de ponta a ponta, sem mexer de lugar nenhum dos arquivos originais: os `.csproj` só apontam pra eles.

## Como rodar

Precisa do .NET 10 SDK e do Docker rodando (o teste sobe um Postgres descartável, em porta aleatória, e apaga no fim).

```bash
cd anexo/07-raiz/portal-minimo
dotnet test
```

Se o Testcontainers não achar o Docker (Docker Desktop no Mac, por exemplo), aponta pro socket: `export DOCKER_HOST=unix://$HOME/.docker/run/docker.sock`.

## O que tem aqui

| Pasta | O que é |
|---|---|
| `src/Portal.Api` | API mínima: `POST /ordens` com `Idempotency-Key`, `RecebimentoMiddleware` na frente, `CriarOrdemHandler`, `OutboxPublisher`. Mais os tipos que faltavam (`Ordem`, `OutboxMensagem`, `PortalDbContext`, `LeituraDbContext`, `RegraDeEnquadramento`, calendário e janela de corte por configuração). |
| `tests/Portal.Tests` | Os testes do anexo (`JanelaDeCorteTests`, `IdempotenciaTests`), o `PortalApiFixture` (WebApplicationFactory + Testcontainers, SQS fake em memória) e o `PortalIntegracaoTests` com o que faltava cobrir. |
| `sql/00-tabelas-base.sql` | As tabelas que o anexo assume que existem (`ordens`, `fundos`, `classes`, `posicoes`). Roda antes do `idempotencia/schema.sql`, que tem FK pra `ordens`. |

## O que os testes provam

- **Corte:** 14:29:59.999 fica no mesmo dia, 14:30:00.000 vai pro próximo dia útil, UTC convertido pra Brasília, feriado, sexta e a regressão do incidente. Também pela API, com o header de chegada.
- **Idempotência, pela API e com Postgres de verdade:** cinco requisições simultâneas com a mesma chave criam uma ordem só; mesma chave com outro corpo dá 422; sem chave dá 400; chaves diferentes são ordens diferentes; primeira em aberto faz a segunda tomar 409 com `Retry-After: 1` em cerca de meio segundo; reenvio depois do commit devolve o mesmo 201 com `Idempotent-Replayed`.
- **Outbox:** a ordem aceita vira uma mensagem no SQS (fake) uma vez só, e a linha fica marcada como publicada.
- **Consulta de posições:** filtro no banco (saldo zero, fundo inativo e outro distribuidor ficam de fora) e paginação por cursor; prévia em lote com resgate acima do saldo e o limite de 500 linhas.

## O que eu corrigi no anexo ao rodar

- `OutboxPublisher`: abria transação explícita com `EnableRetryOnFailure` ligado e quebrava com "does not support user-initiated transactions", então nada era publicado. Passou a rodar dentro da execution strategy, como o `CriarOrdemHandler` já fazia.
- Faltavam `using Microsoft.EntityFrameworkCore;` nos quatro arquivos que usam `AsNoTracking`, `FromSql` e `CreateExecutionStrategy`, e `using Portal.Ordens.Api;` no `IdempotenciaTests`.

## O que é suposição

- A autenticação real é JWT (configurado em `Jwt`); nos testes entra um esquema que autentica como um distribuidor fixo.
- O horário de corte vem da configuração (`Corte:Horario`, padrão 14:30) e os feriados de `Corte:Feriados`. No portal de verdade isso vem do cadastro do fundo.
- Não tem migrations: o schema é o SQL de `sql/` e o `schema.sql` do anexo, igual ao que eu já tinha proposto.
