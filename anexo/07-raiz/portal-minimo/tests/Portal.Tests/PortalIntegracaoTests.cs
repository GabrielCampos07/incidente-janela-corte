using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Portal.Enquadramento;
using Portal.Ordens.Api;
using Portal.Posicoes;
using Xunit;

namespace Portal.Ordens.Tests;

// Cobre o que o IdempotenciaTests não cobre: o 409 com Retry-After de forma determinística, o replay, a mensagem
// chegar no SQS, a data de processamento da ordem criada pela API e a consulta de posições / prévia de enquadramento.
public class PortalIntegracaoTests : IClassFixture<PortalApiFixture>
{
    private readonly PortalApiFixture _api;

    public PortalIntegracaoTests(PortalApiFixture api) => _api = api;

    [Fact]
    public async Task Primeira_requisicao_em_aberto_faz_a_segunda_tomar_409_com_Retry_After()
    {
        var chave = Guid.NewGuid();
        var ordem = new CriarOrdemRequest(PortalApiFixture.FundoTeste, Guid.NewGuid(), TipoOrdem.Aplicacao, 700m);

        // Segura a chave numa transação sem commit, como uma primeira requisição ainda em processamento.
        await using var conn = new NpgsqlConnection(_api.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO idempotencia (distribuidor_id, chave, hash_payload, primeira_chegada) VALUES ($1, $2, '\\x00', now())", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = PortalApiFixture.DistribuidorTeste });
            cmd.Parameters.Add(new NpgsqlParameter { Value = chave });
            await cmd.ExecuteNonQueryAsync();
        }

        var relogio = System.Diagnostics.Stopwatch.StartNew();
        var resposta = await _api.PostOrdem(ordem, chave);
        relogio.Stop();

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), resposta.Headers.RetryAfter?.Delta);
        Assert.InRange(relogio.ElapsedMilliseconds, 400, 3000);   // lock_timeout de 500 ms, sem ficar segurando a conexão
        Assert.Equal(0, await _api.ContarOrdens(ordem.CotistaId));

        await tx.RollbackAsync();   // a primeira falhou sem gravar: o reenvio com a mesma chave passa

        var reenvio = await _api.PostOrdem(ordem, chave);
        Assert.Equal(HttpStatusCode.Created, reenvio.StatusCode);
        Assert.Equal(1, await _api.ContarOrdens(ordem.CotistaId));
    }

    [Fact]
    public async Task Reenvio_com_a_mesma_chave_devolve_o_mesmo_201_marcado_como_replay()
    {
        var chave = Guid.NewGuid();
        var ordem = new CriarOrdemRequest(PortalApiFixture.FundoTeste, Guid.NewGuid(), TipoOrdem.Resgate, 250m);

        var primeira = await _api.PostOrdem(ordem, chave);
        var segunda = await _api.PostOrdem(ordem, chave);

        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);
        Assert.False(primeira.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(HttpStatusCode.Created, segunda.StatusCode);
        Assert.Equal("true", segunda.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(
            (await primeira.Content.ReadFromJsonAsync<OrdemCriadaResponse>())!.Id,
            (await segunda.Content.ReadFromJsonAsync<OrdemCriadaResponse>())!.Id);
        Assert.Equal(1, await _api.ContarOrdens(ordem.CotistaId));
    }

    [Fact]
    public async Task Outbox_publica_a_ordem_aceita_no_SQS_uma_vez()
    {
        var ordem = new CriarOrdemRequest(PortalApiFixture.FundoTeste, Guid.NewGuid(), TipoOrdem.Aplicacao, 1500m);
        var resposta = await _api.PostOrdem(ordem, Guid.NewGuid());
        var criada = (await resposta.Content.ReadFromJsonAsync<OrdemCriadaResponse>())!;

        await Esperar(async () => await _api.Escalar("SELECT publicada_em FROM outbox WHERE agregado_id = $1", criada.Id) is DateTimeOffset or DateTime);

        var mensagens = _api.Sqs.Mensagens.Where(m => m.MessageBody.Contains(criada.Id.ToString())).ToList();
        var mensagem = Assert.Single(mensagens);
        Assert.Equal("OrdemAceita", mensagem.MessageAttributes["tipo"].StringValue);
        Assert.False(string.IsNullOrEmpty(mensagem.MessageAttributes["correlationId"].StringValue));
    }

    [Fact]
    public async Task Ordem_chegou_antes_do_corte_e_a_API_responde_depois_continua_no_mesmo_dia()
    {
        // quinta 22/10, 14h29m40s em Brasília, mesmo com o processamento terminando depois das 14h30
        var chegada = new DateTimeOffset(2026, 10, 22, 14, 29, 40, TimeSpan.FromHours(-3));
        var ordem = new CriarOrdemRequest(PortalApiFixture.FundoTeste, Guid.NewGuid(), TipoOrdem.Aplicacao, 100m);

        var resposta = await _api.PostOrdem(ordem, Guid.NewGuid(), chegada);
        var criada = (await resposta.Content.ReadFromJsonAsync<OrdemCriadaResponse>())!;

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Equal(chegada.ToUniversalTime(), criada.RecebidaEm.ToUniversalTime());
        Assert.Equal(new DateOnly(2026, 10, 22), criada.DataDeProcessamento);
    }

    [Fact]
    public async Task Ordem_exatamente_no_corte_vai_para_o_proximo_dia_util()
    {
        var chegada = new DateTimeOffset(2026, 10, 23, 14, 30, 0, TimeSpan.FromHours(-3));   // sexta
        var ordem = new CriarOrdemRequest(PortalApiFixture.FundoTeste, Guid.NewGuid(), TipoOrdem.Aplicacao, 100m);

        var criada = (await (await _api.PostOrdem(ordem, Guid.NewGuid(), chegada)).Content.ReadFromJsonAsync<OrdemCriadaResponse>())!;

        Assert.Equal(new DateOnly(2026, 10, 26), criada.DataDeProcessamento);   // segunda
    }

    [Fact]
    public async Task Listagem_de_posicoes_filtra_no_banco_e_pagina_por_cursor()
    {
        var (distribuidor, fundoAtivo, fundoInativo, classe) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _api.Executar($"""
            INSERT INTO classes VALUES ('{classe}', 'RF');
            INSERT INTO fundos VALUES ('{fundoAtivo}', 'Ativo', true, '{classe}'), ('{fundoInativo}', 'Inativo', false, '{classe}');
            INSERT INTO posicoes (distribuidor_id, fundo_id, cotista_id, quantidade_cotas, saldo, data_referencia)
            SELECT '{distribuidor}', '{fundoAtivo}', gen_random_uuid(), 10, 100, DATE '2026-10-22' FROM generate_series(1, 5);
            INSERT INTO posicoes (distribuidor_id, fundo_id, cotista_id, quantidade_cotas, saldo, data_referencia)
            VALUES ('{distribuidor}', '{fundoAtivo}', gen_random_uuid(), 0, 0, DATE '2026-10-22'),
                   ('{distribuidor}', '{fundoInativo}', gen_random_uuid(), 10, 100, DATE '2026-10-22'),
                   ('{Guid.NewGuid()}', '{fundoAtivo}', gen_random_uuid(), 10, 100, DATE '2026-10-22');
            """);

        await using var db = NovaLeitura();
        var consulta = new ConsultaPosicoes(db);

        var pagina1 = await consulta.ListarAsync(distribuidor, new FiltroPosicao(null, null, Tamanho: 3), default);
        Assert.Equal(3, pagina1.Itens.Count);
        Assert.NotNull(pagina1.ProximoCursor);

        var pagina2 = await consulta.ListarAsync(distribuidor, new FiltroPosicao(null, pagina1.ProximoCursor, Tamanho: 3), default);
        Assert.Equal(2, pagina2.Itens.Count);          // sem saldo zero, sem fundo inativo, sem outro distribuidor
        Assert.Null(pagina2.ProximoCursor);
        Assert.All(pagina1.Itens.Concat(pagina2.Itens), p => Assert.Equal("RF", p.ClasseCodigo));
        Assert.Equal(5, pagina1.Itens.Concat(pagina2.Itens).Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task Previa_em_lote_barra_resgate_acima_do_saldo()
    {
        var (distribuidor, fundo, classe, cotista) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _api.Executar($"""
            INSERT INTO classes VALUES ('{classe}', 'MM');
            INSERT INTO fundos VALUES ('{fundo}', 'Fundo previa', true, '{classe}');
            INSERT INTO posicoes (distribuidor_id, fundo_id, cotista_id, quantidade_cotas, saldo, data_referencia)
            VALUES ('{distribuidor}', '{fundo}', '{cotista}', 10, 1000, DATE '2026-10-22');
            """);

        await using var db = NovaLeitura();
        var previa = new PreviaEnquadramento(db, new RegraDeEnquadramento());

        var resultado = await previa.AvaliarAsync(distribuidor,
        [
            new LinhaPrevia(1, cotista, fundo, TipoOrdem.Resgate, 600m),
            new LinhaPrevia(2, cotista, fundo, TipoOrdem.Resgate, 600m),   // soma 1200 > saldo 1000
            new LinhaPrevia(3, cotista, fundo, TipoOrdem.Aplicacao, 5000m),
        ], default);

        Assert.Equal([true, false, true], resultado.Select(r => r.Enquadrada));
    }

    [Fact]
    public async Task Previa_com_mais_de_500_linhas_e_recusada()
    {
        await using var db = NovaLeitura();
        var previa = new PreviaEnquadramento(db, new RegraDeEnquadramento());
        var linhas = Enumerable.Range(1, PreviaEnquadramento.MaximoDeLinhas + 1)
            .Select(i => new LinhaPrevia(i, Guid.NewGuid(), Guid.NewGuid(), TipoOrdem.Aplicacao, 1m)).ToList();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => previa.AvaliarAsync(Guid.NewGuid(), linhas, default));
    }

    private LeituraDbContext NovaLeitura() =>
        new(new DbContextOptionsBuilder<LeituraDbContext>().UseNpgsql(_api.ConnectionString).Options);

    private static async Task Esperar(Func<Task<bool>> condicao, int segundos = 15)
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);
        while (DateTime.UtcNow < limite)
        {
            if (await condicao()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Condição não ficou verdadeira a tempo");
    }
}
