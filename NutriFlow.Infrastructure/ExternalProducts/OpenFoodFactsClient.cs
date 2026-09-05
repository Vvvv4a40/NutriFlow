using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.ExternalProducts;

public sealed class OpenFoodFactsClient : IExternalProductProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public OpenFoodFactsClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
    }

    public async Task<Product?> FindByBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken = default)
    {
        string normalizedBarcode = ProductBarcode.Normalize(barcode);
        string requestUri =
            $"api/v3/product/{normalizedBarcode}" +
            "?fields=code,product_name,product_name_ru,nutriments";

        using HttpResponseMessage response = await _httpClient.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        OpenFoodFactsResponse? payload;

        try
        {
            payload = await response.Content.ReadFromJsonAsync<OpenFoodFactsResponse>(
                SerializerOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Open Food Facts returned malformed JSON.",
                exception);
        }

        if (payload?.Product is null)
        {
            return null;
        }

        return MapProduct(payload.Product, normalizedBarcode);
    }

    private static Product MapProduct(
        OpenFoodFactsProduct product,
        string barcode)
    {
        string? productName = FirstNonBlank(
            product.RussianName,
            product.Name);

        if (productName is null || product.Nutriments is null)
        {
            throw IncompleteProduct(barcode);
        }

        OpenFoodFactsNutriments nutriments = product.Nutriments;

        if (nutriments.Calories is null ||
            nutriments.ProteinGrams is null ||
            nutriments.FatGrams is null ||
            nutriments.CarbohydratesGrams is null)
        {
            throw IncompleteProduct(barcode);
        }

        EnsurePlausibleNutrition(nutriments, barcode);

        NutritionValues nutrition = new NutritionValues(
            nutriments.Calories.Value,
            nutriments.ProteinGrams.Value,
            nutriments.FatGrams.Value,
            nutriments.CarbohydratesGrams.Value);
        NutritionSource source = new NutritionSource(
            NutritionSourceKind.ExternalService,
            DataQuality.Unknown,
            "Open Food Facts",
            $"https://world.openfoodfacts.org/product/{barcode}");

        return new Product(productName, nutrition, source, barcode);
    }

    private static void EnsurePlausibleNutrition(
        OpenFoodFactsNutriments nutriments,
        string barcode)
    {
        if (nutriments.Calories is < 0m or > 1000m ||
            nutriments.ProteinGrams is < 0m or > 100m ||
            nutriments.FatGrams is < 0m or > 100m ||
            nutriments.CarbohydratesGrams is < 0m or > 100m)
        {
            throw new InvalidDataException(
                $"Open Food Facts returned implausible nutrition values for barcode '{barcode}'.");
        }
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private static InvalidDataException IncompleteProduct(string barcode)
    {
        return new InvalidDataException(
            $"Open Food Facts has no complete per-100-gram nutrition for barcode '{barcode}'.");
    }

    private sealed record OpenFoodFactsResponse(
        [property: JsonPropertyName("product")]
        OpenFoodFactsProduct? Product);

    private sealed record OpenFoodFactsProduct(
        [property: JsonPropertyName("product_name")]
        string? Name,
        [property: JsonPropertyName("product_name_ru")]
        string? RussianName,
        [property: JsonPropertyName("nutriments")]
        OpenFoodFactsNutriments? Nutriments);

    private sealed record OpenFoodFactsNutriments(
        [property: JsonPropertyName("energy-kcal_100g")]
        [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        decimal? Calories,
        [property: JsonPropertyName("proteins_100g")]
        [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        decimal? ProteinGrams,
        [property: JsonPropertyName("fat_100g")]
        [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        decimal? FatGrams,
        [property: JsonPropertyName("carbohydrates_100g")]
        [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        decimal? CarbohydratesGrams);
}
