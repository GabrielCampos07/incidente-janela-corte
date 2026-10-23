using Microsoft.EntityFrameworkCore;

namespace Portal.Enquadramento;

public sealed record LinhaPrevia(int Linha, Guid CotistaId, Guid FundoId, TipoOrdem Tipo, decimal Valor);

public sealed record SaldoAgregado(Guid CotistaId, Guid FundoId, Guid ClasseId, decimal Saldo);

public sealed record ResultadoPrevia(int Linha, bool Enquadrada, string? Motivo);

/// <summary>
/// Avalia a grade inteira com uma consulta agregada, em vez de uma chamada por linha.
/// A regra é a mesma usada na validação do envio da ordem; aqui o resultado é só informativo.
/// </summary>
public sealed class PreviaEnquadramento
{
    public const int MaximoDeLinhas = 500;

    private readonly LeituraDbContext _db;
    private readonly RegraDeEnquadramento _regra;

    public PreviaEnquadramento(LeituraDbContext db, RegraDeEnquadramento regra)
    {
        _db = db;
        _regra = regra;
    }

    public async Task<IReadOnlyList<ResultadoPrevia>> AvaliarAsync(
        Guid distribuidorId, IReadOnlyList<LinhaPrevia> linhas, CancellationToken ct)
    {
        if (linhas.Count > MaximoDeLinhas)
            throw new ArgumentOutOfRangeException(nameof(linhas), $"Máximo de {MaximoDeLinhas} linhas por prévia");

        var cotistas = linhas.Select(l => l.CotistaId).Distinct().ToArray();

        var saldos = await _db.Posicoes
            .AsNoTracking()
            .Where(p => p.DistribuidorId == distribuidorId
                     && cotistas.Contains(p.CotistaId)
                     && p.Saldo > 0)
            .GroupBy(p => new { p.CotistaId, p.FundoId, p.Fundo.ClasseId })
            .Select(g => new SaldoAgregado(g.Key.CotistaId, g.Key.FundoId, g.Key.ClasseId, g.Sum(p => p.Saldo)))
            .ToListAsync(ct);

        var porCotista = saldos.ToLookup(s => s.CotistaId);

        return linhas
            .Select(l => _regra.Avaliar(l, porCotista[l.CotistaId], linhasDaMesmaGrade: linhas))
            .ToList();
    }
}
