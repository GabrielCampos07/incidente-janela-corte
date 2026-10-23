using System.Text.Json;

namespace Portal.Ordens;

public sealed class OutboxMensagem
{
    public long Id { get; private set; }
    public string Tipo { get; private set; } = "";
    public Guid AgregadoId { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public string Payload { get; private set; } = "{}";
    public DateTimeOffset CriadoEm { get; private set; }
    public DateTimeOffset? PublicadaEm { get; set; }
    public int Tentativas { get; set; }

    private OutboxMensagem() { }

    public static OutboxMensagem OrdemAceita(Ordem o, string correlationId) => new()
    {
        Tipo = "OrdemAceita",
        AgregadoId = o.Id,
        CorrelationId = correlationId,
        Payload = JsonSerializer.Serialize(new
        {
            ordemId = o.Id,
            fundoId = o.FundoId,
            cotistaId = o.CotistaId,
            tipo = o.Tipo.ToString(),
            valor = o.Valor,
            recebidaEm = o.RecebidaEm,
            dataDeProcessamento = o.DataDeProcessamento,
        }),
    };
}

public sealed class IdempotenciaRegistro
{
    public Guid DistribuidorId { get; set; }
    public Guid Chave { get; set; }
    public byte[] HashPayload { get; set; } = [];
    public Guid? OrdemId { get; set; }
    public DateTimeOffset PrimeiraChegada { get; set; }
}
