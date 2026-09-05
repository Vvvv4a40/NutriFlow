using Microsoft.EntityFrameworkCore;

namespace NutriFlow.Infrastructure.Persistence;

public sealed class NutriFlowDbContext : DbContext
{
    public NutriFlowDbContext(DbContextOptions<NutriFlowDbContext> options)
        : base(options)
    {
    }

    internal DbSet<ProductRecord> Products => Set<ProductRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ProductRecord>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(product => product.Id);

            entity.Property(product => product.Name)
                .HasMaxLength(200)
                .IsRequired();
            entity.Property(product => product.NormalizedName)
                .HasMaxLength(200)
                .IsRequired();
            entity.HasIndex(product => product.NormalizedName);
            entity.Property(product => product.Barcode)
                .HasMaxLength(14);
            entity.HasIndex(product => product.Barcode)
                .IsUnique();

            entity.Property(product => product.Calories)
                .HasColumnType("TEXT");
            entity.Property(product => product.ProteinGrams)
                .HasColumnType("TEXT");
            entity.Property(product => product.FatGrams)
                .HasColumnType("TEXT");
            entity.Property(product => product.CarbohydratesGrams)
                .HasColumnType("TEXT");

            entity.Property(product => product.SourceKind)
                .HasConversion<int>();
            entity.Property(product => product.SourceQuality)
                .HasConversion<int>();
            entity.Property(product => product.SourceName)
                .HasMaxLength(200)
                .IsRequired();
            entity.Property(product => product.SourceReference)
                .HasMaxLength(2048);
        });
    }
}
