using System.Net;
using System.Net.Http.Json;
using Portal.Ordens.Api;
using Xunit;

namespace Portal.Ordens.Tests;

// Integração com Postgres real (Testcontainers) e a API em memória (WebApplicationFactory).
// PortalApiFixture sobe o container, aplica as migrations e autentica como um distribuidor de teste.
public class IdempotenciaTests : IClassFixture<PortalApiFixture>
{
    private readonly PortalApiFixture _api;

    public IdempotenciaTests(PortalApiFixture api) => _api = api;

    [Fact]
    public async Task Requisicoes_simultaneas_com_a_mesma_chave_criam_uma_unica_ordem()
    {
        var chave = Guid.NewGuid();
        var ordem = OrdemDeTeste();

        var respostas = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostComRetry(ordem, chave)));

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = await Task.WhenAll(respostas.Select(async r => (await r.Content.ReadFromJsonAsync<OrdemCriadaResponse>())!.Id));
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await _api.ContarOrdens(ordem.CotistaId));
        Assert.Equal(1, await _api.ContarOutbox(ids[0]));
    }

    // Quem chega com a primeira ainda sem commit toma 409 com Retry-After e tenta de novo, como o front faz.
    private async Task<HttpResponseMessage> PostComRetry(CriarOrdemRequest ordem, Guid chave)
    {
        for (var tentativa = 0; tentativa < 8; tentativa++)
        {
            var resposta = await _api.PostOrdem(ordem, chave);
            if (resposta.StatusCode != HttpStatusCode.Conflict) return resposta;

            Assert.Equal(TimeSpan.FromSeconds(1), resposta.Headers.RetryAfter?.Delta);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        throw new TimeoutException("Primeira requisição não terminou a tempo");
    }

    [Fact]
    public async Task Mesma_chave_com_outro_conteudo_retorna_422()
    {
        var chave = Guid.NewGuid();
        await _api.PostOrdem(OrdemDeTeste(valor: 1000m), chave);

        var resposta = await _api.PostOrdem(OrdemDeTeste(valor: 2000m), chave);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
    }

    [Fact]
    public async Task Duas_ordens_iguais_com_chaves_diferentes_sao_duas_ordens()
    {
        var ordem = OrdemDeTeste();

        await _api.PostOrdem(ordem, Guid.NewGuid());
        await _api.PostOrdem(ordem, Guid.NewGuid());

        Assert.Equal(2, await _api.ContarOrdens(ordem.CotistaId));
    }

    [Fact]
    public async Task Sem_chave_retorna_400()
    {
        var resposta = await _api.PostOrdem(OrdemDeTeste(), chave: null);
        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
    }

    private static CriarOrdemRequest OrdemDeTeste(decimal valor = 1000m) =>
        new(FundoId: PortalApiFixture.FundoTeste, CotistaId: Guid.NewGuid(), Tipo: TipoOrdem.Aplicacao, Valor: valor);
}
