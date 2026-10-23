using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;

namespace Portal.Ordens.Infra;

/// <summary>
/// Publica no SQS o que foi gravado na outbox na mesma transação da ordem.
/// Entrega pelo menos uma vez: uma queda entre o envio e o UPDATE reenvia a mensagem,
/// por isso o integrador precisa ser idempotente por ordemId.
/// </summary>
public sealed class OutboxPublisher : BackgroundService
{
    private const int Lote = 10;   // limite do SendMessageBatch

    private readonly IServiceScopeFactory _scopes;
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(IServiceScopeFactory scopes, IAmazonSQS sqs, IConfiguration config, ILogger<OutboxPublisher> logger)
    {
        _scopes = scopes;
        _sqs = sqs;
        _queueUrl = config["Filas:BoletasPendentes"]!;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var publicadas = 0;
            try
            {
                publicadas = await PublicarLoteAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Falha ao publicar lote da outbox");
            }

            if (publicadas < Lote)
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    private async Task<int> PublicarLoteAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        // Com EnableRetryOnFailure no Npgsql, transação explícita precisa rodar dentro da execution strategy.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // SKIP LOCKED permite várias réplicas da API publicando sem pegar a mesma linha.
            var pendentes = await db.Outbox
                .FromSql($"SELECT * FROM outbox WHERE publicada_em IS NULL ORDER BY id LIMIT {Lote} FOR UPDATE SKIP LOCKED")
                .ToListAsync(ct);

            if (pendentes.Count == 0)
                return 0;

            var resposta = await _sqs.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = _queueUrl,
                Entries = pendentes.Select(m => new SendMessageBatchRequestEntry
                {
                    Id = m.Id.ToString(),
                    MessageBody = m.Payload,
                    MessageAttributes = new()
                    {
                        ["correlationId"] = new MessageAttributeValue { DataType = "String", StringValue = m.CorrelationId },
                        ["tipo"] = new MessageAttributeValue { DataType = "String", StringValue = m.Tipo },
                    },
                }).ToList(),
            }, ct);

            var enviadas = resposta.Successful.Select(s => long.Parse(s.Id)).ToHashSet();
            foreach (var m in pendentes)
            {
                if (enviadas.Contains(m.Id)) m.PublicadaEm = DateTimeOffset.UtcNow;
                else m.Tentativas++;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return pendentes.Count;
        });
    }
}
