using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Portal.Ordens;

public class PortalDbContext(DbContextOptions<PortalDbContext> options) : DbContext(options)
{
    public DbSet<Ordem> Ordens => Set<Ordem>();
    public DbSet<OutboxMensagem> Outbox => Set<OutboxMensagem>();
    public DbSet<IdempotenciaRegistro> Idempotencia => Set<IdempotenciaRegistro>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Ordem>(e =>
        {
            e.ToTable("ordens");
            e.HasKey(o => o.Id);
            e.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(o => o.DistribuidorId).HasColumnName("distribuidor_id");
            e.Property(o => o.FundoId).HasColumnName("fundo_id");
            e.Property(o => o.CotistaId).HasColumnName("cotista_id");
            e.Property(o => o.Tipo).HasColumnName("tipo").HasConversion<string>();
            e.Property(o => o.Valor).HasColumnName("valor");
            e.Property(o => o.Status).HasColumnName("status").HasConversion<string>();
            e.Property(o => o.DataDeProcessamento).HasColumnName("data_de_processamento");

            // recebida_em nunca muda depois de gravada
            e.Property(o => o.RecebidaEm).HasColumnName("recebida_em").IsRequired()
                .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        });

        b.Entity<OutboxMensagem>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(m => m.Id);
            e.Property(m => m.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(m => m.Tipo).HasColumnName("tipo");
            e.Property(m => m.AgregadoId).HasColumnName("agregado_id");
            e.Property(m => m.CorrelationId).HasColumnName("correlation_id");
            e.Property(m => m.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(m => m.CriadoEm).HasColumnName("criado_em").ValueGeneratedOnAdd();
            e.Property(m => m.PublicadaEm).HasColumnName("publicada_em");
            e.Property(m => m.Tentativas).HasColumnName("tentativas");
        });

        b.Entity<IdempotenciaRegistro>(e =>
        {
            e.ToTable("idempotencia");
            e.HasKey(i => new { i.DistribuidorId, i.Chave });
            e.Property(i => i.DistribuidorId).HasColumnName("distribuidor_id");
            e.Property(i => i.Chave).HasColumnName("chave");
            e.Property(i => i.HashPayload).HasColumnName("hash_payload");
            e.Property(i => i.OrdemId).HasColumnName("ordem_id");
            e.Property(i => i.PrimeiraChegada).HasColumnName("primeira_chegada");
        });
    }
}
