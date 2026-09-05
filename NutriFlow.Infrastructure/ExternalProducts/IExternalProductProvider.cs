using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.ExternalProducts;

public interface IExternalProductProvider
{
    Task<Product?> FindByBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken = default);
}
