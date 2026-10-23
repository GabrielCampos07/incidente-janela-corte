using System.Security.Cryptography;
using System.Text.Json;
using Portal.Ordens.Dominio;

namespace Portal.Ordens;

public sealed record CriarOrdemRequest(Guid FundoId, Guid CotistaId, TipoOrdem Tipo, decimal Valor);

public interface IJanelasDeCorte
{
    Task<JanelaDeCorte> DoFundoAsync(Guid fundoId, CancellationToken ct);
}

/// <summary>
/// Horário de corte vindo da configuração. No portal real isso vem do cadastro do fundo (regulamento).
/// </summary>
public sealed class JanelasDeCorte(IConfiguration config, ICalendarioDiasUteis calendario) : IJanelasDeCorte
{
    public Task<JanelaDeCorte> DoFundoAsync(Guid fundoId, CancellationToken ct)
    {
        var horario = TimeOnly.Parse(config["Corte:Horario"] ?? "14:30");
        return Task.FromResult(new JanelaDeCorte(horario, calendario));
    }
}

/// <summary>Fim de semana e feriados da configuração (Corte:Feriados, datas yyyy-MM-dd).</summary>
public sealed class CalendarioDiasUteis(IConfiguration config) : ICalendarioDiasUteis
{
    private readonly HashSet<DateOnly> _feriados =
        (config.GetSection("Corte:Feriados").Get<string[]>() ?? []).Select(DateOnly.Parse).ToHashSet();

    public bool EhDiaUtil(DateOnly data) =>
        data.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !_feriados.Contains(data);

    public DateOnly ProximoDiaUtil(DateOnly data)
    {
        do data = data.AddDays(1); while (!EhDiaUtil(data));
        return data;
    }
}

public static class PayloadHash
{
    // Mesmo corpo, mesmo hash: o record serializa sempre na mesma ordem.
    public static byte[] Sha256(CriarOrdemRequest req) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(req));
}
