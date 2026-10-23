using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Portal.Ordens;
using Portal.Ordens.Api;
using Testcontainers.PostgreSql;
using Xunit;

namespace Portal.Ordens.Tests;

/// <summary>
/// Sobe um Postgres descartável (Testcontainers, porta aleatória), aplica o SQL de base e o schema.sql do anexo,
/// e levanta a API em memória (WebApplicationFactory) autenticada como um distribuidor de teste.
/// O SQS é um fake em memória, e o OutboxPublisher real roda contra ele.
/// </summary>
public sealed class PortalApiFixture : IAsyncLifetime
{
    public static readonly Guid FundoTeste = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid DistribuidorTeste = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public const string FilaTeste = "http://sqs.test/boletas-pendentes";

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private WebApplicationFactory<Program> _fabrica = null!;
    private HttpClient _http = null!;

    public FakeSqs Sqs { get; } = FakeSqs.Criar();
    public string ConnectionString => _pg.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();

        foreach (var arquivo in new[] { "00-tabelas-base.sql", "10-schema.sql" })
            await Executar(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", arquivo)));

        _fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Portal"] = ConnectionString,
                ["Filas:BoletasPendentes"] = FilaTeste,
            }));

            web.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAmazonSQS>();
                services.AddSingleton<IAmazonSQS>(Sqs.Cliente);

                services.AddAuthentication(o =>
                {
                    o.DefaultScheme = TesteAuthHandler.Esquema;
                    o.DefaultAuthenticateScheme = TesteAuthHandler.Esquema;
                    o.DefaultChallengeScheme = TesteAuthHandler.Esquema;
                }).AddScheme<AuthenticationSchemeOptions, TesteAuthHandler>(TesteAuthHandler.Esquema, _ => { });
            });
        });

        _http = _fabrica.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _fabrica.DisposeAsync();
        await _pg.DisposeAsync();
    }

    // O API Gateway injeta o header com o instante de chegada; aqui o teste faz o mesmo.
    public Task<HttpResponseMessage> PostOrdem(CriarOrdemRequest ordem, Guid? chave, DateTimeOffset? recebidaEm = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/ordens") { Content = JsonContent.Create(ordem) };
        req.Headers.Add(RecebimentoMiddleware.Header, (recebidaEm ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds().ToString());
        if (chave is { } k) req.Headers.Add(CriarOrdemHandler.HeaderChave, k.ToString());
        return _http.SendAsync(req);
    }

    public async Task<int> ContarOrdens(Guid cotistaId) =>
        Convert.ToInt32(await Escalar("SELECT count(*) FROM ordens WHERE cotista_id = $1", cotistaId));

    public async Task<int> ContarOutbox(Guid ordemId) =>
        Convert.ToInt32(await Escalar("SELECT count(*) FROM outbox WHERE agregado_id = $1", ordemId));

    public async Task<object?> Escalar(string sql, params object[] parametros)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parametros) cmd.Parameters.Add(new NpgsqlParameter { Value = p });
        return await cmd.ExecuteScalarAsync();
    }

    public async Task Executar(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class TesteAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Esquema = "Teste";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identidade = new ClaimsIdentity([new Claim("distribuidor_id", PortalApiFixture.DistribuidorTeste.ToString())], Esquema);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidade), Esquema)));
    }
}

/// <summary>SQS em memória: só implementa SendMessageBatchAsync, o que o OutboxPublisher usa.</summary>
public class FakeSqs : DispatchProxy
{
    public ConcurrentQueue<SendMessageBatchRequestEntry> Mensagens { get; } = new();
    public IAmazonSQS Cliente => (IAmazonSQS)(object)this;

    public static FakeSqs Criar() => (FakeSqs)(object)Create<IAmazonSQS, FakeSqs>();

    protected override object? Invoke(MethodInfo? metodo, object?[]? args)
    {
        switch (metodo?.Name)
        {
            case "SendMessageBatchAsync":
                var pedido = (SendMessageBatchRequest)args![0]!;
                foreach (var e in pedido.Entries) Mensagens.Enqueue(e);
                return Task.FromResult(new SendMessageBatchResponse
                {
                    Successful = pedido.Entries.Select(e => new SendMessageBatchResultEntry { Id = e.Id }).ToList(),
                });
            case "Dispose":
                return null;
            default:
                throw new NotSupportedException(metodo?.Name);
        }
    }
}
