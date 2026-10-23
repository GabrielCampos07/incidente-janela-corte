using System.Security.Claims;
using Amazon.SQS;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Portal.Ordens;
using Portal.Ordens.Api;
using Portal.Ordens.Dominio;
using Portal.Ordens.Infra;

var builder = WebApplication.CreateBuilder(args);

// A connection string é lida só quando o DbContext é criado, pra os testes conseguirem trocar por um container.
builder.Services.AddDbContext<PortalDbContext>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Portal"), n => n.EnableRetryOnFailure()));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<Recebimento>();
builder.Services.AddScoped<IRecebimento>(sp => sp.GetRequiredService<Recebimento>());
builder.Services.AddSingleton<ICalendarioDiasUteis, CalendarioDiasUteis>();
builder.Services.AddSingleton<IJanelasDeCorte, JanelasDeCorte>();
builder.Services.AddScoped<CriarOrdemHandler>();

builder.Services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient());
builder.Services.AddHostedService<OutboxPublisher>();

builder.Services.AddAuthentication("Bearer").AddJwtBearer(o => builder.Configuration.GetSection("Jwt").Bind(o));
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseMiddleware<RecebimentoMiddleware>();   // antes de autenticação e de qualquer acesso ao banco
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/ordens", (CriarOrdemRequest req, [FromHeader(Name = CriarOrdemHandler.HeaderChave)] string? chave,
                        ClaimsPrincipal user, HttpResponse response, CriarOrdemHandler handler, CancellationToken ct)
    => handler.HandleAsync(user.DistribuidorId(), chave, req, response, ct))
   .RequireAuthorization();

app.Run();

static class UserExtensions
{
    public static Guid DistribuidorId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue("distribuidor_id") ?? throw new InvalidOperationException("Token sem distribuidor_id"));
}

public partial class Program;
