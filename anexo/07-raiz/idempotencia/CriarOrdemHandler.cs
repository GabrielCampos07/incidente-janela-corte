using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Portal.Ordens.Api;

public sealed class CriarOrdemHandler
{
    public const string HeaderChave = "Idempotency-Key";

    private readonly PortalDbContext _db;
    private readonly IRecebimento _recebimento;
    private readonly IJanelasDeCorte _janelas;

    public CriarOrdemHandler(PortalDbContext db, IRecebimento recebimento, IJanelasDeCorte janelas)
    {
        _db = db;
        _recebimento = recebimento;
        _janelas = janelas;
    }

    public async Task<IResult> HandleAsync(
        Guid distribuidorId, string? chaveHeader, CriarOrdemRequest req, HttpResponse response, CancellationToken ct)
    {
        if (!Guid.TryParse(chaveHeader, out var chave))
            return Results.Problem(statusCode: 400, title: $"Header {HeaderChave} obrigatório (UUID)");

        var hash = PayloadHash.Sha256(req);

        // Com EnableRetryOnFailure no Npgsql, transação explícita precisa rodar dentro da execution strategy.
        return await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // lock_timeout curto: a segunda requisição com a mesma chave não fica segurando conexão
            // esperando a primeira terminar, que é justamente o que esgota o pool.
            await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '500ms'", ct);

            int inserida;
            try
            {
                inserida = await _db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO idempotencia (distribuidor_id, chave, hash_payload, primeira_chegada)
                    VALUES ({distribuidorId}, {chave}, {hash}, {_recebimento.RecebidaEm})
                    ON CONFLICT (distribuidor_id, chave) DO NOTHING
                    """, ct);
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                // a primeira requisição com essa chave ainda não deu commit: o cliente tenta de novo
                response.Headers.RetryAfter = "1";
                return Results.Problem(statusCode: 409, title: "Requisição com essa chave ainda em processamento");
            }

            if (inserida == 0)
            {
                // a primeira já deu commit: devolve o mesmo 201 (ou 422 se o corpo mudou)
                await tx.RollbackAsync(ct);
                return await ReplayAsync(distribuidorId, chave, hash, response, ct);
            }

            var janela = await _janelas.DoFundoAsync(req.FundoId, ct);
            var ordem = Ordem.Criar(distribuidorId, req, _recebimento.RecebidaEm, janela);

            _db.Ordens.Add(ordem);
            _db.Outbox.Add(OutboxMensagem.OrdemAceita(ordem, correlationId: response.HttpContext.TraceIdentifier));
            await _db.SaveChangesAsync(ct);

            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE idempotencia SET ordem_id = {ordem.Id}
                WHERE distribuidor_id = {distribuidorId} AND chave = {chave}
                """, ct);

            await tx.CommitAsync(ct);
            return Results.Created($"/ordens/{ordem.Id}", OrdemCriadaResponse.De(ordem));
        });
    }

    private async Task<IResult> ReplayAsync(
        Guid distribuidorId, Guid chave, byte[] hash, HttpResponse response, CancellationToken ct)
    {
        var registro = await _db.Idempotencia.AsNoTracking()
            .SingleAsync(i => i.DistribuidorId == distribuidorId && i.Chave == chave, ct);

        if (!registro.HashPayload.AsSpan().SequenceEqual(hash))
            return Results.Problem(statusCode: 422, title: $"{HeaderChave} já usada com outro conteúdo");

        var ordem = await _db.Ordens.AsNoTracking().SingleAsync(o => o.Id == registro.OrdemId, ct);

        response.Headers["Idempotent-Replayed"] = "true";
        return Results.Created($"/ordens/{ordem.Id}", OrdemCriadaResponse.De(ordem));
    }
}

// Resposta inclui o horário oficial de chegada: é a prova que o distribuidor guarda.
public sealed record OrdemCriadaResponse(Guid Id, string Status, DateTimeOffset RecebidaEm, DateOnly DataDeProcessamento)
{
    public static OrdemCriadaResponse De(Ordem o) => new(o.Id, o.Status.ToString(), o.RecebidaEm, o.DataDeProcessamento);
}

// Program.cs
// app.MapPost("/ordens", (CriarOrdemRequest req, [FromHeader(Name = CriarOrdemHandler.HeaderChave)] string? chave,
//                         ClaimsPrincipal user, HttpResponse response, CriarOrdemHandler handler, CancellationToken ct)
//     => handler.HandleAsync(user.DistribuidorId(), chave, req, response, ct));
