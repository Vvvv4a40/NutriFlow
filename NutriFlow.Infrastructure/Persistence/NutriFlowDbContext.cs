using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;

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
    internal DbSet<LabelPhotoRecord> LabelPhotos => Set<LabelPhotoRecord>();
    internal DbSet<SavedDishRecord> SavedDishes => Set<SavedDishRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UserRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.CreatedAtUtc).HasColumnType("TEXT");
            entity.Property(user => user.LegacyLabelPhotosImported).HasDefaultValue(false);
            entity.HasIndex(user => user.IsLegacyLocal)
                .IsUnique()
                .HasFilter("\"IsLegacyLocal\" = 1");
        });

        modelBuilder.Entity<LabelPhotoRecord>(entity =>
        {
            entity.ToTable("LabelPhotos");
            entity.HasKey(photo => photo.FileName);
            entity.Property(photo => photo.FileName).HasMaxLength(37);
            entity.Property(photo => photo.RegisteredAtUtc).HasColumnType("TEXT");
            entity.HasOne<UserRecord>()
                .WithMany()
                .HasForeignKey(photo => photo.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProductRecord>(entity =>
        {
            entity.ToTable("Products");
            entity.HasKey(product => product.Id);
            entity.HasOne<UserRecord>()
                .WithMany()
                .HasForeignKey(product => product.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(product => product.Name)
                .HasMaxLength(200)
                .IsRequired();
            entity.Property(product => product.NormalizedName)
                .HasMaxLength(200)
                .IsRequired();
            entity.HasIndex(product => new { product.UserId, product.NormalizedName });
            entity.Property(product => product.Barcode)
                .HasMaxLength(14);
            entity.HasIndex(product => product.Barcode)
                .IsUnique()
                .HasFilter("\"UserId\" IS NULL AND \"Barcode\" IS NOT NULL");
            entity.HasIndex(product => new { product.UserId, product.Barcode })
                .IsUnique()
                .HasFilter("\"UserId\" IS NOT NULL AND \"Barcode\" IS NOT NULL");

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
            entity.HasOne<UserRecord>()
                .WithMany()
                .HasForeignKey(alias => alias.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(alias => alias.Name)
                .HasMaxLength(200)
                .IsRequired();
            entity.Property(alias => alias.NormalizedName)
                .HasMaxLength(200)
                .IsRequired();
            entity.HasIndex(alias => new { alias.UserId, alias.NormalizedName });
            entity.HasIndex(alias => new
            {
                alias.UserId,
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
            entity.Property(session => session.Purpose)
                .HasConversion<int>()
                .HasDefaultValue(MealSessionPurpose.Diary);
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

        modelBuilder.Entity<SavedDishRecord>(entity =>
        {
            entity.ToTable("SavedDishes");
            entity.HasKey(dish => dish.Id);
            entity.HasOne<UserRecord>()
                .WithMany()
                .HasForeignKey(dish => dish.UserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MealSessionRecord>()
                .WithMany()
                .HasForeignKey(dish => dish.SourceSessionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(dish => dish.SourceSessionId).IsUnique();
            entity.Property(dish => dish.Name)
                .HasMaxLength(200)
                .IsRequired();
            entity.Property(dish => dish.NormalizedName)
                .HasMaxLength(200)
                .IsRequired();
            entity.HasIndex(dish => new { dish.UserId, dish.NormalizedName }).IsUnique();
            entity.Property(dish => dish.FinalWeightInGrams).HasColumnType("TEXT");
            entity.Property(dish => dish.Calories).HasColumnType("TEXT");
            entity.Property(dish => dish.ProteinGrams).HasColumnType("TEXT");
            entity.Property(dish => dish.FatGrams).HasColumnType("TEXT");
            entity.Property(dish => dish.CarbohydratesGrams).HasColumnType("TEXT");
            entity.Property(dish => dish.Quality).HasConversion<int>();
            entity.Property(dish => dish.CreatedAtUtc).HasColumnType("TEXT");
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
