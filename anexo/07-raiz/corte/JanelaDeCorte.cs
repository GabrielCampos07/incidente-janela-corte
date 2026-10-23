namespace Portal.Ordens.Dominio;

public interface ICalendarioDiasUteis
{
    bool EhDiaUtil(DateOnly data);
    DateOnly ProximoDiaUtil(DateOnly data);
}

/// <summary>
/// Decide a data de processamento a partir do instante em que a requisição chegou,
/// nunca a partir do instante em que a ordem foi gravada.
/// O horário de corte vem do regulamento de cada fundo.
/// </summary>
public sealed class JanelaDeCorte
{
    private static readonly TimeZoneInfo Brasilia = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private readonly TimeOnly _horarioCorte;
    private readonly ICalendarioDiasUteis _calendario;

    public JanelaDeCorte(TimeOnly horarioCorte, ICalendarioDiasUteis calendario)
    {
        _horarioCorte = horarioCorte;
        _calendario = calendario;
    }

    // Recebida exatamente no horário de corte (14:30:00.000) já vai para o próximo dia útil.
    public DateOnly DataDeProcessamento(DateTimeOffset recebidaEm)
    {
        var local = TimeZoneInfo.ConvertTime(recebidaEm, Brasilia).DateTime;
        var data = DateOnly.FromDateTime(local);

        if (!_calendario.EhDiaUtil(data) || TimeOnly.FromDateTime(local) >= _horarioCorte)
            return _calendario.ProximoDiaUtil(data);

        return data;
    }
}
