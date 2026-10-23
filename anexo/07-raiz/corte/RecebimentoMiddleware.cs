namespace Portal.Ordens.Api;

public interface IRecebimento
{
    DateTimeOffset RecebidaEm { get; }
}

public sealed class Recebimento : IRecebimento
{
    public DateTimeOffset RecebidaEm { get; set; }
}

/// <summary>
/// Primeiro middleware do pipeline. O API Gateway injeta X-Recebida-Em-Epoch a partir de
/// $context.requestTimeEpoch (mapeamento de integração), sobrescrevendo qualquer valor enviado pelo cliente.
/// A API só é alcançável pela rede privada do Gateway, então o header é confiável.
/// </summary>
public sealed class RecebimentoMiddleware
{
    public const string Header = "X-Recebida-Em-Epoch";

    private readonly RequestDelegate _next;
    private readonly TimeProvider _relogio;
    private readonly ILogger<RecebimentoMiddleware> _logger;

    public RecebimentoMiddleware(RequestDelegate next, TimeProvider relogio, ILogger<RecebimentoMiddleware> logger)
    {
        _next = next;
        _relogio = relogio;
        _logger = logger;
    }

    public Task InvokeAsync(HttpContext context, Recebimento recebimento)
    {
        if (context.Request.Headers.TryGetValue(Header, out var valor)
            && long.TryParse(valor, out var epochMs))
        {
            recebimento.RecebidaEm = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);
        }
        else
        {
            recebimento.RecebidaEm = _relogio.GetUtcNow();
            _logger.LogWarning("Requisição sem {Header}; usando o relógio da aplicação", Header);
        }

        return _next(context);
    }
}

// Program.cs
// builder.Services.AddSingleton(TimeProvider.System);
// builder.Services.AddScoped<Recebimento>();
// builder.Services.AddScoped<IRecebimento>(sp => sp.GetRequiredService<Recebimento>());
// app.UseMiddleware<RecebimentoMiddleware>();   // antes de autenticação e de qualquer acesso ao banco
//
// OrdemConfiguration (EF Core): recebida_em nunca muda depois de gravada
// builder.Property(o => o.RecebidaEm).HasColumnName("recebida_em").IsRequired()
//        .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
