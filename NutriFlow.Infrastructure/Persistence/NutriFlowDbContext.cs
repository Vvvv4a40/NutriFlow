using Microsoft.EntityFrameworkCore;

namespace NutriFlow.Infrastructure.Persistence;

public sealed class NutriFlowDbContext : DbContext
{
    public NutriFlowDbContext(DbContextOptions<NutriFlowDbContext> options)
        : base(options)
    {
    }

    internal DbSet<ProductRecord> Products => Set<ProductRecord>();
    internal DbSet<ProductAliasRecord> ProductAliases => Set<ProductAliasRecord>();
    internal DbSet<MealSessionRecord> MealSessions => Set<MealSessionRecord>();
    internal DbSet<MealEntryRecord> MealEntries => Set<MealEntryRecord>();
    internal DbSet<DailyGoalRecord> DailyGoals => Set<DailyGoalRecord>();
    internal DbSet<UserRecord> Users => Set<UserRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UserRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.CreatedAtUtc).HasColumnType("TEXT");
            entity.HasIndex(user => user.IsLegacyLocal)
                .IsUnique()
                .HasFilter("\"IsLegacyLocal\" = 1");
        });

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

        modelBuilder.Entity<ProductAliasRecord>(entity =>
        {
            entity.ToTable("ProductAliases");
            entity.HasKey(alias => alias.Id);
            entity.Property(alias => alias.Name)
                .HasMaxLength(200)
                .IsRequired();
            entity.Property(alias => alias.NormalizedName)
                .HasMaxLength(200)
                .IsRequired();
            entity.HasIndex(alias => alias.NormalizedName);
            entity.HasIndex(alias => new
            {
                alias.ProductId,
                alias.NormalizedName
            }).IsUnique();
            entity.HasOne(alias => alias.Product)
                .WithMany(product => product.Aliases)
                .HasForeignKey(alias => alias.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MealSessionRecord>(entity =>
        {
            entity.ToTable("MealSessions");
            entity.HasKey(session => session.Id);
            entity.HasOne<UserRecord>()
                .WithMany()
                .HasForeignKey(session => session.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(session => session.MessagesJson).IsRequired();
            entity.Property(session => session.DraftJson).IsRequired();
            entity.Property(session => session.PreviewJson).IsRequired();
            entity.Property(session => session.PreviewToken)
                .HasMaxLength(64)
                .IsRequired();
            entity.Property(session => session.Status).HasConversion<int>();
            entity.Property(session => session.MealDate).HasColumnType("TEXT");
            entity.Property(session => session.CreatedAtUtc).HasColumnType("TEXT");
            entity.Property(session => session.UpdatedAtUtc).HasColumnType("TEXT");
            entity.Property(session => session.ConfirmedAtUtc).HasColumnType("TEXT");
            entity.HasIndex(session => new
            {
                session.UserId,
                session.IdempotencyKey
            }).IsUnique();
            entity.Property(session => session.OriginalRequestHash)
                .HasMaxLength(64);
            entity.Property(session => session.MessageRequestHashesJson)
                .HasDefaultValue("{}")
                .IsRequired();
        });

        modelBuilder.Entity<MealEntryRecord>(entity =>
        {
            entity.ToTable("MealEntries");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.Name)
                .HasMaxLength(200)
                .IsRequired();
            entity.Property(entry => entry.MealDate).HasColumnType("TEXT");
            entity.Property(entry => entry.WeightInGrams).HasColumnType("TEXT");
            entity.Property(entry => entry.Calories).HasColumnType("TEXT");
            entity.Property(entry => entry.ProteinGrams).HasColumnType("TEXT");
            entity.Property(entry => entry.FatGrams).HasColumnType("TEXT");
            entity.Property(entry => entry.CarbohydratesGrams).HasColumnType("TEXT");
            entity.Property(entry => entry.Quality).HasConversion<int>();
            entity.Property(entry => entry.CreatedAtUtc).HasColumnType("TEXT");
            entity.HasIndex(entry => new
            {
                entry.MealSessionId,
                entry.Sequence
            }).IsUnique();
            entity.HasIndex(entry => entry.MealDate);
            entity.HasOne(entry => entry.MealSession)
                .WithMany(session => session.MealEntries)
                .HasForeignKey(entry => entry.MealSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DailyGoalRecord>(entity =>
        {
            entity.ToTable("DailyGoals");
            entity.HasKey(goal => new { goal.UserId, goal.Date });
            entity.HasOne<UserRecord>()
                .WithMany()
                .HasForeignKey(goal => goal.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(goal => goal.Date).HasColumnType("TEXT");
            entity.Property(goal => goal.Calories).HasColumnType("TEXT");
            entity.Property(goal => goal.ProteinGrams).HasColumnType("TEXT");
            entity.Property(goal => goal.FatGrams).HasColumnType("TEXT");
            entity.Property(goal => goal.CarbohydratesGrams).HasColumnType("TEXT");
        });
    }
}
