using Microsoft.EntityFrameworkCore;

namespace Portal;

public class Classe { public Guid Id { get; set; } public string Codigo { get; set; } = ""; }

public class Fundo
{
    public Guid Id { get; set; }
    public string Nome { get; set; } = "";
    public bool Ativo { get; set; }
    public Guid ClasseId { get; set; }
    public Classe Classe { get; set; } = null!;
}

public class Posicao
{
    public long Id { get; set; }
    public Guid DistribuidorId { get; set; }
    public Guid FundoId { get; set; }
    public Fundo Fundo { get; set; } = null!;
    public Guid CotistaId { get; set; }
    public decimal QuantidadeCotas { get; set; }
    public decimal Saldo { get; set; }
    public DateOnly DataReferencia { get; set; }
}

/// <summary>Contexto só de leitura (no portal real aponta pra réplica, com pool separado).</summary>
public class LeituraDbContext(DbContextOptions<LeituraDbContext> options) : DbContext(options)
{
    public DbSet<Posicao> Posicoes => Set<Posicao>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Classe>(e => { e.ToTable("classes"); e.Property(c => c.Id).HasColumnName("id"); e.Property(c => c.Codigo).HasColumnName("codigo"); });
        b.Entity<Fundo>(e =>
        {
            e.ToTable("fundos");
            e.Property(f => f.Id).HasColumnName("id"); e.Property(f => f.Nome).HasColumnName("nome");
            e.Property(f => f.Ativo).HasColumnName("ativo"); e.Property(f => f.ClasseId).HasColumnName("classe_id");
            e.HasOne(f => f.Classe).WithMany().HasForeignKey(f => f.ClasseId);
        });
        b.Entity<Posicao>(e =>
        {
            e.ToTable("posicoes");
            e.Property(p => p.Id).HasColumnName("id"); e.Property(p => p.DistribuidorId).HasColumnName("distribuidor_id");
            e.Property(p => p.FundoId).HasColumnName("fundo_id"); e.Property(p => p.CotistaId).HasColumnName("cotista_id");
            e.Property(p => p.QuantidadeCotas).HasColumnName("quantidade_cotas"); e.Property(p => p.Saldo).HasColumnName("saldo");
            e.Property(p => p.DataReferencia).HasColumnName("data_referencia");
            e.HasOne(p => p.Fundo).WithMany().HasForeignKey(p => p.FundoId);
        });
    }
}
