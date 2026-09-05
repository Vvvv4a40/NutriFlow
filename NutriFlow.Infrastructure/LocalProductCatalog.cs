using System.Text;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure;

public sealed class LocalProductCatalog
{
    private readonly NutriFlowDbContext _dbContext;

    public LocalProductCatalog(NutriFlowDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
    }

    public async Task<bool> AddAsync(
        Product product,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);

        string normalizedName = NormalizeName(product.Name);
        bool alreadyExists = await _dbContext.Products
            .AsNoTracking()
            .AnyAsync(
                record =>
                    (product.Barcode != null &&
                     record.Barcode == product.Barcode) ||
                    (record.NormalizedName == normalizedName &&
                     record.Calories == product.NutritionPer100Grams.Calories &&
                     record.ProteinGrams == product.NutritionPer100Grams.ProteinGrams &&
                     record.FatGrams == product.NutritionPer100Grams.FatGrams &&
                     record.CarbohydratesGrams ==
                         product.NutritionPer100Grams.CarbohydratesGrams &&
                     record.SourceKind == product.Source.Kind &&
                     record.SourceQuality == product.Source.Quality &&
                     record.SourceName == product.Source.Name &&
                     record.SourceReference == product.Source.Reference),
                cancellationToken);

        if (alreadyExists)
        {
            return false;
        }

        _dbContext.Products.Add(MapRecord(product));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (product.Barcode is not null)
        {
            _dbContext.ChangeTracker.Clear();

            bool barcodeWasAddedConcurrently = await _dbContext.Products
                .AsNoTracking()
                .AnyAsync(
                    record => record.Barcode == product.Barcode,
                    cancellationToken);

            if (barcodeWasAddedConcurrently)
            {
                return false;
            }

            throw;
        }

        return true;
    }

    public async Task<IReadOnlyList<Product>> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        string normalizedName = NormalizeName(name);

        List<ProductRecord> records = await _dbContext.Products
            .AsNoTracking()
            .Where(product => product.NormalizedName == normalizedName)
            .OrderBy(product => product.Id)
            .ToListAsync(cancellationToken);

        return records.Select(MapProduct).ToArray();
    }

    public async Task<Product?> FindByBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        string normalizedBarcode = ProductBarcode.Normalize(barcode);

        ProductRecord? record = await _dbContext.Products
            .AsNoTracking()
            .SingleOrDefaultAsync(
                product => product.Barcode == normalizedBarcode,
                cancellationToken);

        return record is null ? null : MapProduct(record);
    }

    private static ProductRecord MapRecord(Product product)
    {
        return new ProductRecord
        {
            Name = product.Name,
            NormalizedName = NormalizeName(product.Name),
            Barcode = product.Barcode,
            Calories = product.NutritionPer100Grams.Calories,
            ProteinGrams = product.NutritionPer100Grams.ProteinGrams,
            FatGrams = product.NutritionPer100Grams.FatGrams,
            CarbohydratesGrams = product.NutritionPer100Grams.CarbohydratesGrams,
            SourceKind = product.Source.Kind,
            SourceQuality = product.Source.Quality,
            SourceName = product.Source.Name,
            SourceReference = product.Source.Reference
        };
    }

    private static Product MapProduct(ProductRecord record)
    {
        NutritionValues nutrition = new NutritionValues(
            record.Calories,
            record.ProteinGrams,
            record.FatGrams,
            record.CarbohydratesGrams);
        NutritionSource source = new NutritionSource(
            record.SourceKind,
            record.SourceQuality,
            record.SourceName,
            record.SourceReference);

        return new Product(record.Name, nutrition, source, record.Barcode);
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return name
            .Trim()
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();
    }
}
