using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Portal.Ordens.Api;
using Portal.Ordens.Dominio;
using Xunit;

namespace Portal.Ordens.Tests;

public class JanelaDeCorteTests
{
    private static readonly TimeSpan Brasilia = TimeSpan.FromHours(-3);
    private readonly JanelaDeCorte _janela = new(new TimeOnly(14, 30), new CalendarioFake(feriados: [new DateOnly(2026, 11, 2)]));

    [Fact]
    public void Um_milissegundo_antes_do_corte_entra_no_mesmo_dia()
    {
        var recebida = new DateTimeOffset(2026, 10, 22, 14, 29, 59, 999, Brasilia);
        Assert.Equal(new DateOnly(2026, 10, 22), _janela.DataDeProcessamento(recebida));
    }

    [Fact]
    public void Exatamente_no_corte_vai_para_o_proximo_dia_util()
    {
        var recebida = new DateTimeOffset(2026, 10, 22, 14, 30, 0, Brasilia);
        Assert.Equal(new DateOnly(2026, 10, 23), _janela.DataDeProcessamento(recebida));
    }

    [Fact]
    public void Instante_em_UTC_e_convertido_para_Brasilia()
    {
        // 17:29:59Z = 14:29:59 em Brasília. A task no Fargate roda em UTC.
        var recebida = new DateTimeOffset(2026, 10, 22, 17, 29, 59, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 22), _janela.DataDeProcessamento(recebida));
    }

    [Fact]
    public void Sexta_depois_do_corte_vai_para_segunda()
    {
        var recebida = new DateTimeOffset(2026, 10, 23, 14, 31, 0, Brasilia);
        Assert.Equal(new DateOnly(2026, 10, 26), _janela.DataDeProcessamento(recebida));
    }

    [Fact]
    public void Feriado_vai_para_o_proximo_dia_util()
    {
        var recebida = new DateTimeOffset(2026, 11, 2, 10, 0, 0, Brasilia);
        Assert.Equal(new DateOnly(2026, 11, 3), _janela.DataDeProcessamento(recebida));
    }

    // Regressão do incidente de 22/10: a requisição chega antes do corte e o processamento
    // termina depois. A data tem que sair do instante de chegada.
    [Fact]
    public async Task Processamento_lento_nao_muda_a_data_da_ordem()
    {
        var relogio = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 14, 29, 40, Brasilia));
        var chegadaNoGateway = new DateTimeOffset(2026, 10, 22, 14, 29, 40, Brasilia);
        var recebimento = new Recebimento();
        DateOnly? dataDecidida = null;

        var middleware = new RecebimentoMiddleware(
            next: _ =>
            {
                relogio.Advance(TimeSpan.FromSeconds(45));   // espera pelo pool + commit lento
                dataDecidida = _janela.DataDeProcessamento(recebimento.RecebidaEm);
                return Task.CompletedTask;
            },
            relogio,
            NullLogger<RecebimentoMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Headers[RecebimentoMiddleware.Header] = chegadaNoGateway.ToUnixTimeMilliseconds().ToString();

        await middleware.InvokeAsync(context, recebimento);

        Assert.True(relogio.GetUtcNow() > new DateTimeOffset(2026, 10, 22, 14, 30, 0, Brasilia));
        Assert.Equal(new DateOnly(2026, 10, 22), dataDecidida);
    }

    private sealed class CalendarioFake(DateOnly[] feriados) : ICalendarioDiasUteis
    {
        public bool EhDiaUtil(DateOnly d) =>
            d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !feriados.Contains(d);

        public DateOnly ProximoDiaUtil(DateOnly d)
        {
            do d = d.AddDays(1); while (!EhDiaUtil(d));
            return d;
        }
    }
}
