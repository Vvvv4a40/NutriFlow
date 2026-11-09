using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure;

public sealed class LocalProductCatalog
{
    private const int MaximumRecentHints = 100;
    private const int MaximumMatchedHints = 50;
    private const int MaximumHintWords = 30;

    private readonly NutriFlowDbContext _dbContext;
    private readonly Guid _ownerId;

    public LocalProductCatalog(NutriFlowDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        _dbContext = dbContext;
        _ownerId = dbContext.Users
            .AsNoTracking()
            .Where(user => user.IsLegacyLocal)
            .Select(user => user.Id)
            .Single();
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

    public async Task<bool> AddAsync(
        Product product,
        CancellationToken cancellationToken = default)
    {
        return await AddStoredAsync(product, cancellationToken) is not null;
    }

    // null when the same product is already there, or its barcode has data of equal or better quality
    public async Task<StoredProduct?> AddStoredAsync(
        Product product,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);

        int? id = await AddCoreAsync(product, _ownerId, cancellationToken);
        return id is null ? null : await FindByIdAsync(id.Value, cancellationToken);
    }

    internal async Task<bool> AddSharedExternalProductAsync(
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

        return await AddCoreAsync(product, null, cancellationToken) is not null;
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

        ProductAliasRecord addedAlias = new ProductAliasRecord
        {
            UserId = _ownerId,
            ProductId = record.Id,
            Name = alias.Trim(),
            NormalizedName = normalizedAlias
        };
        _dbContext.ProductAliases.Add(addedAlias);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _dbContext.Entry(addedAlias).State = EntityState.Detached;
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
        catch
        {
            _dbContext.Entry(addedAlias).State = EntityState.Detached;
            throw;
        }

        return MapProduct(record);
    }

    public async Task<string> CreateSelectionAliasAsync(
        Product selected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        string normalizedName = NormalizeName(selected.Name);
        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        List<ProductRecord> candidates = await VisibleForSelection()
            .Where(product => product.NormalizedName == normalizedName && product.Barcode == selected.Barcode)
            .OrderBy(product => product.Id)
            .ToListAsync(cancellationToken);
        ProductRecord record = candidates.FirstOrDefault(candidate =>
                MatchesSelection(MapProduct(candidate), selected))
            ?? throw SelectionUnavailable();
        return await AddSelectionAliasAsync(record, transaction, cancellationToken);
    }

    public async Task<string> CreateSelectionAliasAsync(
        int productId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        ProductRecord record = await VisibleForSelection()
            .SingleOrDefaultAsync(product => product.Id == productId, cancellationToken)
            ?? throw SelectionUnavailable();
        return await AddSelectionAliasAsync(record, transaction, cancellationToken);
    }

    private async Task<string> AddSelectionAliasAsync(
        ProductRecord record,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        string alias = $"selected-{Guid.NewGuid():N}";
        ProductAliasRecord addedAlias = new()
        {
            UserId = _ownerId,
            ProductId = record.Id,
            Name = alias,
            NormalizedName = NormalizeName(alias)
        };
        _dbContext.ProductAliases.Add(addedAlias);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return alias;
        }
        catch
        {
            _dbContext.Entry(addedAlias).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<IReadOnlyList<Product>> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        return (await FindStoredByNameAsync(name, cancellationToken))
            .Select(product => product.Product)
            .ToArray();
    }

    public async Task<IReadOnlyList<StoredProduct>> FindStoredByNameAsync(
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
            return records.Select(MapStored).ToArray();
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
            .Select(MapStored)
            .ToArray();
    }

    public async Task<IReadOnlyList<Product>> SearchAsync(
        string? query = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return (await SearchStoredAsync(query, limit, cancellationToken))
            .Select(product => product.Product)
            .ToArray();
    }

    public async Task<IReadOnlyList<StoredProduct>> SearchStoredAsync(
        string? query = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The search limit must be between 1 and 200.");
        }

        IQueryable<ProductRecord> matches = _dbContext.Products
            .AsNoTracking()
            .Where(product => product.UserId == null || product.UserId == _ownerId);
        if (!string.IsNullOrWhiteSpace(query))
        {
            string normalizedQuery = NormalizeName(query);
            matches = matches.Where(product =>
                product.NormalizedName.Contains(normalizedQuery) ||
                (product.Barcode != null && product.Barcode.Contains(normalizedQuery)) ||
                product.Aliases.Any(alias => alias.UserId == _ownerId &&
                                            alias.NormalizedName.Contains(normalizedQuery)));
        }

        IQueryable<ProductRecord> withoutSharedOverrides = matches.Where(product =>
            product.UserId != null || product.Barcode == null ||
            !_dbContext.Products.Any(personal => personal.UserId == _ownerId &&
                                                personal.Barcode == product.Barcode));
        IQueryable<ProductRecord> personalOverrides = _dbContext.Products
            .AsNoTracking()
            .Where(personal => personal.UserId == _ownerId && personal.Barcode != null &&
                matches.Any(shared => shared.UserId == null && shared.Barcode == personal.Barcode));
        List<ProductRecord> records = await withoutSharedOverrides
            .Union(personalOverrides)
            .OrderBy(product => product.NormalizedName)
            .ThenBy(product => product.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return records.Select(MapStored).ToArray();
    }

    public async Task<Product?> FindByBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        return (await FindStoredByBarcodeAsync(barcode, cancellationToken))?.Product;
    }

    public async Task<StoredProduct?> FindStoredByBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        string normalizedBarcode = ProductBarcode.Normalize(barcode);

        ProductRecord? record = await FindVisibleBarcodeRecordAsync(
            normalizedBarcode, cancellationToken);

        return record is null ? null : MapStored(record);
    }

    // a shared record hidden by a personal version of its barcode is not visible: the personal one replaces it
    public async Task<StoredProduct?> FindByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        ProductRecord? record = await VisibleForSelection()
            .SingleOrDefaultAsync(product => product.Id == id, cancellationToken);

        return record is null ? null : MapStored(record);
    }

    // what the model is told about the catalog: the products added last and those named like a word of the text.
    // The whole catalog would cost tokens on every request and still miss what was just typed
    public async Task<IReadOnlyList<string>> GetModelHintsAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<string> names = await _dbContext.Products
            .AsNoTracking()
            .Where(product => product.UserId == _ownerId)
            .OrderByDescending(product => product.Id)
            .Select(product => product.Name)
            .Take(MaximumRecentHints)
            .ToListAsync(cancellationToken);
        HashSet<string> seen = new(StringComparer.Ordinal);
        names = names.Where(seen.Add).ToList();
        int matched = 0;

        foreach (string stem in HintStems(text))
        {
            List<string> found = await VisibleForSelection()
                .Where(product => product.NormalizedName.Contains(stem))
                .OrderByDescending(product => product.Id)
                .Select(product => product.Name)
                .Take(MaximumMatchedHints)
                .ToListAsync(cancellationToken);

            foreach (string name in found.Where(seen.Add))
            {
                names.Add(name);
                if (++matched == MaximumMatchedHints)
                {
                    return names;
                }
            }
        }

        return names;
    }

    private async Task<int?> AddCoreAsync(
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
                .SingleOrDefaultAsync(
                    record => record.UserId == userId && record.Barcode == product.Barcode,
                    cancellationToken);

        if (barcodeMatch is not null)
        {
            return await UpgradeBarcodeProductAsync(
                barcodeMatch,
                product,
                cancellationToken)
                ? barcodeMatch.Id
                : null;
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
            return null;
        }

        ProductRecord addedRecord = MapRecord(product, userId);
        _dbContext.Products.Add(addedRecord);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (product.Barcode is not null)
        {
            _dbContext.Entry(addedRecord).State = EntityState.Detached;

            ProductRecord? concurrentlyAdded = await _dbContext.Products
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.UserId == userId && record.Barcode == product.Barcode,
                    cancellationToken);

            if (concurrentlyAdded is not null)
            {
                return await UpgradeBarcodeProductAsync(
                    concurrentlyAdded,
                    product,
                    cancellationToken)
                    ? concurrentlyAdded.Id
                    : null;
            }

            throw;
        }
        catch
        {
            _dbContext.Entry(addedRecord).State = EntityState.Detached;
            throw;
        }

        return addedRecord.Id;
    }

    private static KeyNotFoundException SelectionUnavailable() =>
        new("Выбранный продукт недоступен или изменился. Обновите каталог и выберите его заново.");

    private IQueryable<ProductRecord> VisibleForSelection()
    {
        return _dbContext.Products
            .AsNoTracking()
            .Where(product => product.UserId == _ownerId ||
                (product.UserId == null && (product.Barcode == null ||
                    !_dbContext.Products.Any(personal => personal.UserId == _ownerId &&
                                                        personal.Barcode == product.Barcode))));
    }

    private static IEnumerable<string> HintStems(string text)
    {
        // a short stem finds other forms of the word: "гречки" finds "гречка", "яйца" finds "яйцо"
        return Regex.Matches(text.Normalize(NormalizationForm.FormC), @"\p{L}+")
            .Select(match => match.Value.ToUpperInvariant())
            .Where(word => word.Length >= 3)
            .Select(word => word[..Math.Max(3, Math.Min(5, word.Length - 1))])
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumHintWords);
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
        while (QualityRank(incoming.Source.Quality) > QualityRank(existing.SourceQuality))
        {
            if (await TryUpgradeBarcodeProductAsync(existing, incoming, cancellationToken))
            {
                return true;
            }

            existing = await _dbContext.Products
                .AsNoTracking()
                .SingleAsync(
                    record => record.Id == existing.Id && record.UserId == existing.UserId,
                    cancellationToken);
        }

        return false;
    }

    private async Task<bool> TryUpgradeBarcodeProductAsync(
        ProductRecord existing,
        Product incoming,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(cancellationToken);
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
            return false;
        }

        ProductAliasRecord? addedAlias = null;
        try
        {
            if (existing.NormalizedName != incomingNormalizedName &&
                !await _dbContext.ProductAliases.AnyAsync(
                    alias => alias.UserId == _ownerId &&
                             alias.ProductId == existing.Id &&
                             alias.NormalizedName == existing.NormalizedName,
                    cancellationToken))
            {
                addedAlias = new ProductAliasRecord
                {
                    UserId = _ownerId,
                    ProductId = existing.Id,
                    Name = existing.Name,
                    NormalizedName = existing.NormalizedName
                };
                _dbContext.ProductAliases.Add(addedAlias);

                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            if (addedAlias is not null)
            {
                _dbContext.Entry(addedAlias).State = EntityState.Detached;
            }

            throw;
        }
    }

    private static StoredProduct MapStored(ProductRecord record) => new(record.Id, MapProduct(record));

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

    private static bool MatchesSelection(Product candidate, Product selected)
    {
        return candidate.Name == selected.Name && candidate.Barcode == selected.Barcode &&
               candidate.NutritionPer100Grams.Calories == selected.NutritionPer100Grams.Calories &&
               candidate.NutritionPer100Grams.ProteinGrams == selected.NutritionPer100Grams.ProteinGrams &&
               candidate.NutritionPer100Grams.FatGrams == selected.NutritionPer100Grams.FatGrams &&
               candidate.NutritionPer100Grams.CarbohydratesGrams == selected.NutritionPer100Grams.CarbohydratesGrams &&
               candidate.Source.Kind == selected.Source.Kind && candidate.Source.Quality == selected.Source.Quality &&
               candidate.Source.Name == selected.Source.Name && candidate.Source.Reference == selected.Source.Reference;
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
