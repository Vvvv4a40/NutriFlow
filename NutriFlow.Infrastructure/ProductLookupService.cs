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

        if (externalProduct.Barcode != normalizedBarcode)
        {
            throw new InvalidDataException("The external product barcode does not match the request.");
        }

        await _localCatalog.AddSharedExternalProductAsync(
            externalProduct,
            cancellationToken);

        return await _localCatalog.FindByBarcodeAsync(
                   normalizedBarcode,
                   cancellationToken) ??
               throw new InvalidDataException("The imported product is not available in the catalogue.");
    }
}
