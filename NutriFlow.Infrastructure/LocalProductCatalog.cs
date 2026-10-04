using System.Text;
using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure;

public sealed class LocalProductCatalog
{
    private readonly NutriFlowDbContext _dbContext;
    private readonly Guid _ownerId;

    public LocalProductCatalog(NutriFlowDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
        _ownerId = dbContext.Users
            .AsNoTracking()
            .Single(user => user.IsLegacyLocal)
            .Id;
    }

    public LocalProductCatalog(NutriFlowDbContext dbContext, Guid ownerId)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException("An owner ID cannot be empty.", nameof(ownerId));
        }

        _dbContext = dbContext;
        _ownerId = ownerId;
    }

    public Task<bool> AddAsync(
        Product product,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);

        return AddCoreAsync(product, _ownerId, cancellationToken);
    }

    internal Task<bool> AddSharedExternalProductAsync(
        Product product,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);

        if (product.Source.Kind != NutritionSourceKind.ExternalService ||
            !Uri.TryCreate(product.Source.Reference, UriKind.Absolute, out Uri? sourceUri) ||
            (sourceUri.Scheme != Uri.UriSchemeHttp &&
             sourceUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException(
                "A shared import requires an external product with a public HTTP source.");
        }

        return AddCoreAsync(product, null, cancellationToken);
    }

    private async Task<bool> AddCoreAsync(
        Product product,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);

        string normalizedName = NormalizeName(product.Name);
        ProductRecord? barcodeMatch = product.Barcode is null
            ? null
            : await _dbContext.Products
                .AsNoTracking()
                .Include(record => record.Aliases.Where(alias => alias.UserId == _ownerId))
                .SingleOrDefaultAsync(
                    record => record.UserId == userId && record.Barcode == product.Barcode,
                    cancellationToken);

        if (barcodeMatch is not null)
        {
            return await UpgradeBarcodeProductAsync(
                barcodeMatch,
                product,
                cancellationToken);
        }

        bool alreadyExists = await _dbContext.Products
            .AsNoTracking()
            .AnyAsync(
                record =>
                    record.UserId == userId &&
                    record.NormalizedName == normalizedName &&
                      record.Calories == product.NutritionPer100Grams.Calories &&
                     record.ProteinGrams == product.NutritionPer100Grams.ProteinGrams &&
                     record.FatGrams == product.NutritionPer100Grams.FatGrams &&
                     record.CarbohydratesGrams ==
                         product.NutritionPer100Grams.CarbohydratesGrams &&
                     record.SourceKind == product.Source.Kind &&
                      record.SourceQuality == product.Source.Quality &&
                      record.SourceName == product.Source.Name &&
                      record.SourceReference == product.Source.Reference &&
                      record.Barcode == product.Barcode,
                cancellationToken);

        if (alreadyExists)
        {
            return false;
        }

        _dbContext.Products.Add(MapRecord(product, userId));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (product.Barcode is not null)
        {
            _dbContext.ChangeTracker.Clear();

            ProductRecord? concurrentlyAdded = await _dbContext.Products
                .AsNoTracking()
                .Include(record => record.Aliases.Where(alias => alias.UserId == _ownerId))
                .SingleOrDefaultAsync(
                    record => record.UserId == userId && record.Barcode == product.Barcode,
                    cancellationToken);

            if (concurrentlyAdded is not null)
            {
                return await UpgradeBarcodeProductAsync(
                    concurrentlyAdded,
                    product,
                    cancellationToken);
            }

            throw;
        }

        return true;
    }

    public async Task<Product> AddAliasByBarcodeAsync(
        string barcode,
        string alias,
        CancellationToken cancellationToken = default)
    {
        string normalizedBarcode = ProductBarcode.Normalize(barcode);
        string normalizedAlias = NormalizeName(alias);
        ProductRecord record = await FindVisibleBarcodeRecordAsync(
            normalizedBarcode, cancellationToken) ??
            throw new KeyNotFoundException(
                $"Product with barcode '{normalizedBarcode}' was not found.");

        bool aliasExists = await _dbContext.ProductAliases
            .AsNoTracking()
            .AnyAsync(
                item => item.UserId == _ownerId &&
                        item.ProductId == record.Id &&
                        item.NormalizedName == normalizedAlias,
                cancellationToken);

        if (record.NormalizedName == normalizedAlias || aliasExists)
        {
            return MapProduct(record);
        }

        _dbContext.ProductAliases.Add(new ProductAliasRecord
        {
            UserId = _ownerId,
            ProductId = record.Id,
            Name = alias.Trim(),
            NormalizedName = normalizedAlias
        });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _dbContext.ChangeTracker.Clear();
            bool aliasWasAddedConcurrently = await _dbContext.ProductAliases
                .AsNoTracking()
                .AnyAsync(
                    item =>
                        item.UserId == _ownerId &&
                        item.ProductId == record.Id &&
                        item.NormalizedName == normalizedAlias,
                    cancellationToken);

            if (!aliasWasAddedConcurrently)
            {
                throw;
            }

            record = await _dbContext.Products
                .AsNoTracking()
                .SingleAsync(
                    product => product.Id == record.Id &&
                               (product.UserId == null || product.UserId == _ownerId),
                    cancellationToken);
        }

        return MapProduct(record);
    }

    public async Task<IReadOnlyList<Product>> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        string normalizedName = NormalizeName(name);

        List<ProductRecord> records = await _dbContext.Products
            .AsNoTracking()
            .Where(product => product.UserId == null || product.UserId == _ownerId)
            .Where(product =>
                product.NormalizedName == normalizedName ||
                product.Aliases.Any(alias =>
                    alias.UserId == _ownerId && alias.NormalizedName == normalizedName))
            .OrderBy(product => product.Id)
            .ToListAsync(cancellationToken);

        string[] sharedBarcodes = records
            .Where(record => record.UserId is null && record.Barcode is not null)
            .Select(record => record.Barcode!)
            .ToArray();

        if (sharedBarcodes.Length == 0)
        {
            return records.Select(MapProduct).ToArray();
        }

        Dictionary<string, ProductRecord> personalOverrides = await _dbContext.Products
            .AsNoTracking()
            .Where(product => product.UserId == _ownerId &&
                              product.Barcode != null &&
                              sharedBarcodes.Contains(product.Barcode))
            .ToDictionaryAsync(product => product.Barcode!, cancellationToken);

        // личная версия того же штрихкода сохраняет поиск по имени и алиасам общей записи
        return records
            .Select(record => record.UserId is null &&
                              record.Barcode is not null &&
                              personalOverrides.TryGetValue(record.Barcode, out ProductRecord? personal)
                ? personal
                : record)
            .DistinctBy(record => record.Id)
            .Select(MapProduct)
            .ToArray();
    }

    public async Task<Product?> FindByBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        string normalizedBarcode = ProductBarcode.Normalize(barcode);

        ProductRecord? record = await FindVisibleBarcodeRecordAsync(
            normalizedBarcode, cancellationToken);

        return record is null ? null : MapProduct(record);
    }

    private Task<ProductRecord?> FindVisibleBarcodeRecordAsync(
        string barcode,
        CancellationToken cancellationToken)
    {
        return _dbContext.Products
            .AsNoTracking()
            .Where(product => product.Barcode == barcode &&
                              (product.UserId == _ownerId || product.UserId == null))
            .OrderBy(product => product.UserId == null)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static ProductRecord MapRecord(Product product, Guid? userId)
    {
        return new ProductRecord
        {
            UserId = userId,
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

    private async Task<bool> UpgradeBarcodeProductAsync(
        ProductRecord existing,
        Product incoming,
        CancellationToken cancellationToken)
    {
        if (QualityRank(incoming.Source.Quality) <=
            QualityRank(existing.SourceQuality))
        {
            return false;
        }

        string incomingNormalizedName = NormalizeName(incoming.Name);
        int updatedCount = await _dbContext.Products
            .Where(record =>
                record.Id == existing.Id &&
                record.UserId == existing.UserId &&
                record.SourceQuality == existing.SourceQuality)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(record => record.Name, incoming.Name)
                    .SetProperty(
                        record => record.NormalizedName,
                        incomingNormalizedName)
                    .SetProperty(
                        record => record.Calories,
                        incoming.NutritionPer100Grams.Calories)
                    .SetProperty(
                        record => record.ProteinGrams,
                        incoming.NutritionPer100Grams.ProteinGrams)
                    .SetProperty(
                        record => record.FatGrams,
                        incoming.NutritionPer100Grams.FatGrams)
                    .SetProperty(
                        record => record.CarbohydratesGrams,
                        incoming.NutritionPer100Grams.CarbohydratesGrams)
                    .SetProperty(record => record.SourceKind, incoming.Source.Kind)
                    .SetProperty(
                        record => record.SourceQuality,
                        incoming.Source.Quality)
                    .SetProperty(record => record.SourceName, incoming.Source.Name)
                    .SetProperty(
                        record => record.SourceReference,
                        incoming.Source.Reference),
                cancellationToken);

        if (updatedCount == 0)
        {
            _dbContext.ChangeTracker.Clear();
            ProductRecord latest = await _dbContext.Products
                .AsNoTracking()
                .Include(record => record.Aliases.Where(alias => alias.UserId == _ownerId))
                .SingleAsync(
                    record => record.Id == existing.Id && record.UserId == existing.UserId,
                    cancellationToken);

            return await UpgradeBarcodeProductAsync(
                latest,
                incoming,
                cancellationToken);
        }

        if (existing.NormalizedName != incomingNormalizedName &&
            existing.Aliases.All(alias =>
                alias.UserId != _ownerId || alias.NormalizedName != existing.NormalizedName))
        {
            _dbContext.ProductAliases.Add(new ProductAliasRecord
            {
                UserId = _ownerId,
                ProductId = existing.Id,
                Name = existing.Name,
                NormalizedName = existing.NormalizedName
            });

            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                _dbContext.ChangeTracker.Clear();
                bool aliasExists = await _dbContext.ProductAliases
                    .AsNoTracking()
                    .AnyAsync(
                        alias =>
                            alias.UserId == _ownerId &&
                            alias.ProductId == existing.Id &&
                            alias.NormalizedName == existing.NormalizedName,
                        cancellationToken);

                if (!aliasExists)
                {
                    throw;
                }
            }
        }

        return true;
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

        string normalized = name
            .Trim()
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();

        if (normalized.Length > 200)
        {
            throw new ArgumentException(
                "A product name or alias cannot exceed 200 characters.",
                nameof(name));
        }

        return normalized;
    }

    private static int QualityRank(DataQuality quality)
    {
        return quality switch
        {
            DataQuality.Exact => 3,
            DataQuality.Verified => 2,
            DataQuality.Estimated => 1,
            _ => 0
        };
    }
}
