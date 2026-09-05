using NutriFlow.Domain;
using NutriFlow.Infrastructure.ExternalProducts;

namespace NutriFlow.Infrastructure;

public sealed class ProductLookupService
{
    private readonly LocalProductCatalog _localCatalog;
    private readonly IExternalProductProvider _externalProvider;

    public ProductLookupService(
        LocalProductCatalog localCatalog,
        IExternalProductProvider externalProvider)
    {
        ArgumentNullException.ThrowIfNull(localCatalog);
        ArgumentNullException.ThrowIfNull(externalProvider);

        _localCatalog = localCatalog;
        _externalProvider = externalProvider;
    }

    public async Task<Product?> FindByBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        string normalizedBarcode = ProductBarcode.Normalize(barcode);
        Product? localProduct = await _localCatalog.FindByBarcodeAsync(
            normalizedBarcode,
            cancellationToken);

        if (localProduct is not null)
        {
            return localProduct;
        }

        Product? externalProduct = await _externalProvider.FindByBarcodeAsync(
            normalizedBarcode,
            cancellationToken);

        if (externalProduct is null)
        {
            return null;
        }

        bool wasAdded = await _localCatalog.AddAsync(
            externalProduct,
            cancellationToken);

        if (wasAdded)
        {
            return externalProduct;
        }

        return await _localCatalog.FindByBarcodeAsync(
                   normalizedBarcode,
                   cancellationToken) ??
               externalProduct;
    }
}
