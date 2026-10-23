using Portal.Ordens.Dominio;

namespace Portal.Ordens;

public enum StatusOrdem { Aceita }

public sealed class Ordem
{
    public Guid Id { get; private set; }
    public Guid DistribuidorId { get; private set; }
    public Guid FundoId { get; private set; }
    public Guid CotistaId { get; private set; }
    public TipoOrdem Tipo { get; private set; }
    public decimal Valor { get; private set; }
    public StatusOrdem Status { get; private set; }

    // Instante de chegada no API Gateway. Imutável depois de gravada.
    public DateTimeOffset RecebidaEm { get; private set; }
    public DateOnly DataDeProcessamento { get; private set; }

    private Ordem() { }

    public static Ordem Criar(Guid distribuidorId, CriarOrdemRequest req, DateTimeOffset recebidaEm, JanelaDeCorte janela)
    {
        if (req.Valor <= 0)
            throw new ArgumentOutOfRangeException(nameof(req), "Valor da ordem deve ser positivo");

        return new Ordem
        {
            Id = Guid.CreateVersion7(),
            DistribuidorId = distribuidorId,
            FundoId = req.FundoId,
            CotistaId = req.CotistaId,
            Tipo = req.Tipo,
            Valor = req.Valor,
            Status = StatusOrdem.Aceita,
            RecebidaEm = recebidaEm,
            DataDeProcessamento = janela.DataDeProcessamento(recebidaEm),
        };
    }
}
