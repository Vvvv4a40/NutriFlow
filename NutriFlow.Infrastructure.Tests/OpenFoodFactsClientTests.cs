using System.Net;
using System.Text;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.ExternalProducts;

namespace NutriFlow.Infrastructure.Tests;

public sealed class OpenFoodFactsClientTests
{
    [Fact]
    public async Task FindByBarcodeAsync_WithCompleteProduct_MapsProductAndSource()
    {
        StubHttpMessageHandler handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {
              "product": {
                "product_name": "Original name",
                "product_name_ru": " Тестовый продукт ",
                "nutriments": {
                  "energy-kcal_100g": 123.45,
                  "proteins_100g": "6.7",
                  "fat_100g": 8.9,
                  "carbohydrates_100g": 10.11
                }
              }
            }
            """);
        OpenFoodFactsClient client = CreateClient(handler);

        Product product = Assert.IsType<Product>(
            await client.FindByBarcodeAsync("3017620422003"));

        Assert.Equal("Тестовый продукт", product.Name);
        Assert.Equal("3017620422003", product.Barcode);
        Assert.Equal(123.45m, product.NutritionPer100Grams.Calories);
        Assert.Equal(6.7m, product.NutritionPer100Grams.ProteinGrams);
        Assert.Equal(8.9m, product.NutritionPer100Grams.FatGrams);
        Assert.Equal(10.11m, product.NutritionPer100Grams.CarbohydratesGrams);
        Assert.Equal(NutritionSourceKind.ExternalService, product.Source.Kind);
        Assert.Equal(DataQuality.Unknown, product.Source.Quality);
        Assert.Equal("Open Food Facts", product.Source.Name);
        Assert.Equal(
            "https://world.openfoodfacts.org/product/3017620422003",
            product.Source.Reference);
        Assert.Equal(
            "api/v3/product/3017620422003?fields=code,product_name,product_name_ru,nutriments",
            handler.LastRequestUri?.PathAndQuery.TrimStart('/'));
    }

    [Fact]
    public async Task FindByBarcodeAsync_WhenProductDoesNotExist_ReturnsNull()
    {
        StubHttpMessageHandler handler = new StubHttpMessageHandler(
            HttpStatusCode.NotFound,
            "{}");
        OpenFoodFactsClient client = CreateClient(handler);

        Product? product = await client.FindByBarcodeAsync("3017620422003");

        Assert.Null(product);
    }

    [Fact]
    public async Task FindByBarcodeAsync_WithIncompleteNutrition_ThrowsInvalidDataException()
    {
        StubHttpMessageHandler handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {
              "product": {
                "product_name": "Incomplete product",
                "nutriments": {
                  "energy-kcal_100g": 100,
                  "proteins_100g": 4,
                  "fat_100g": 2
                }
              }
            }
            """);
        OpenFoodFactsClient client = CreateClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.FindByBarcodeAsync("3017620422003"));
    }

    [Fact]
    public async Task FindByBarcodeAsync_WithImplausibleNutrition_ThrowsInvalidDataException()
    {
        StubHttpMessageHandler handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            """
            {
              "product": {
                "product_name": "Invalid product",
                "nutriments": {
                  "energy-kcal_100g": 1200,
                  "proteins_100g": 4,
                  "fat_100g": 2,
                  "carbohydrates_100g": 10
                }
              }
            }
            """);
        OpenFoodFactsClient client = CreateClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.FindByBarcodeAsync("3017620422003"));
    }

    [Fact]
    public async Task FindByBarcodeAsync_WhenServiceFails_ThrowsHttpRequestException()
    {
        StubHttpMessageHandler handler = new StubHttpMessageHandler(
            HttpStatusCode.TooManyRequests,
            "{}");
        OpenFoodFactsClient client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.FindByBarcodeAsync("3017620422003"));
    }

    [Fact]
    public async Task FindByBarcodeAsync_WithInvalidBarcode_DoesNotSendRequest()
    {
        StubHttpMessageHandler handler = new StubHttpMessageHandler(
            HttpStatusCode.OK,
            "{}");
        OpenFoodFactsClient client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.FindByBarcodeAsync("not-a-barcode"));

        Assert.Equal(0, handler.CallCount);
    }

    private static OpenFoodFactsClient CreateClient(
        HttpMessageHandler handler)
    {
        HttpClient httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://world.openfoodfacts.org/")
        };

        return new OpenFoodFactsClient(httpClient);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public StubHttpMessageHandler(
            HttpStatusCode statusCode,
            string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        public int CallCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;

            HttpResponseMessage response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(
                    _content,
                    Encoding.UTF8,
                    "application/json")
            };

            return Task.FromResult(response);
        }
    }
}
