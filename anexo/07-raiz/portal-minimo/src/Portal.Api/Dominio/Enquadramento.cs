using Portal.Enquadramento;

namespace Portal;

public enum TipoOrdem { Aplicacao, Resgate }

/// <summary>
/// Regra mínima de enquadramento: resgate não pode passar do saldo do cotista no fundo,
/// somando os outros resgates da mesma grade. Aplicação sempre enquadra.
/// </summary>
public sealed class RegraDeEnquadramento
{
    public ResultadoPrevia Avaliar(LinhaPrevia linha, IEnumerable<SaldoAgregado> saldosDoCotista, IReadOnlyList<LinhaPrevia> linhasDaMesmaGrade)
    {
        if (linha.Tipo == TipoOrdem.Aplicacao)
            return new ResultadoPrevia(linha.Linha, true, null);

        var saldo = saldosDoCotista.Where(s => s.FundoId == linha.FundoId).Sum(s => s.Saldo);
        var resgatado = linhasDaMesmaGrade
            .Where(l => l.Tipo == TipoOrdem.Resgate && l.CotistaId == linha.CotistaId && l.FundoId == linha.FundoId && l.Linha <= linha.Linha)
            .Sum(l => l.Valor);

        return resgatado <= saldo
            ? new ResultadoPrevia(linha.Linha, true, null)
            : new ResultadoPrevia(linha.Linha, false, "Resgate acima do saldo");
    }
}
