using Microsoft.EntityFrameworkCore;

namespace Portal.Posicoes;

public sealed record FiltroPosicao(Guid? FundoId, long? AposId, int Tamanho = 50);

public sealed record PosicaoDto(
    long Id, Guid FundoId, string FundoNome, string ClasseCodigo,
    Guid CotistaId, decimal QuantidadeCotas, decimal Saldo, DateOnly DataReferencia);

public sealed record Pagina<T>(IReadOnlyList<T> Itens, long? ProximoCursor);

public sealed class ConsultaPosicoes
{
    private const int TamanhoMaximo = 200;

    private readonly LeituraDbContext _db;

    public ConsultaPosicoes(LeituraDbContext db) => _db = db;

    public async Task<Pagina<PosicaoDto>> ListarAsync(Guid distribuidorId, FiltroPosicao filtro, CancellationToken ct)
    {
        var tamanho = Math.Clamp(filtro.Tamanho, 1, TamanhoMaximo);

        var query = _db.Posicoes
            .AsNoTracking()
            .Where(p => p.DistribuidorId == distribuidorId
                     && p.Saldo > 0
                     && p.Fundo.Ativo);

        if (filtro.FundoId is { } fundoId)
            query = query.Where(p => p.FundoId == fundoId);

        if (filtro.AposId is { } aposId)
            query = query.Where(p => p.Id > aposId);

        var itens = await query
            .OrderBy(p => p.Id)
            .Select(p => new PosicaoDto(
                p.Id, p.FundoId, p.Fundo.Nome, p.Fundo.Classe.Codigo,
                p.CotistaId, p.QuantidadeCotas, p.Saldo, p.DataReferencia))
            .Take(tamanho + 1)
            .ToListAsync(ct);

        var temMais = itens.Count > tamanho;
        if (temMais) itens.RemoveAt(itens.Count - 1);

        return new Pagina<PosicaoDto>(itens, temMais ? itens[^1].Id : null);
    }
}
