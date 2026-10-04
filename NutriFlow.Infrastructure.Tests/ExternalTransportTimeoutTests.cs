using System.Net;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Ai;
using NutriFlow.Infrastructure.ExternalProducts;

namespace NutriFlow.Infrastructure.Tests;

public sealed class ExternalTransportTimeoutTests
{
    [Theory]
    [InlineData(nameof(GroqMealParser))]
    [InlineData(nameof(GroqNutritionLabelReader))]
    [InlineData(nameof(OpenAiMealParser))]
    [InlineData(nameof(OpenAiNutritionLabelReader))]
    [InlineData(nameof(OpenFoodFactsClient))]
    public async Task WhenResponseBodyStalls_RespectsClientTimeout(string provider)
    {
        using HttpClient client = new(new DelayedBodyHttpMessageHandler())
        {
            BaseAddress = new Uri("https://provider.test/"),
            Timeout = TimeSpan.FromMilliseconds(100)
        };
        CaptureSession session = new();
        session.AddEvent(new InputEvent("Message"));
        session.FinishCollecting();

        Func<Task> sendRequest = provider switch
        {
            nameof(GroqMealParser) => () => new GroqMealParser(client, "test-model")
                .ParseAsync(session),
            nameof(GroqNutritionLabelReader) => () => new GroqNutritionLabelReader(client, "test-model")
                .ReadAsync(new byte[] { 1 }, "image/jpeg"),
            nameof(OpenAiMealParser) => () => new OpenAiMealParser(client, "test-model")
                .ParseAsync(session),
            nameof(OpenAiNutritionLabelReader) => () => new OpenAiNutritionLabelReader(client, "test-model")
                .ReadAsync(new byte[] { 1 }, "image/jpeg"),
            nameof(OpenFoodFactsClient) => () => new OpenFoodFactsClient(client)
                .FindByBarcodeAsync("3017620422003"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sendRequest().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class DelayedBodyHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new DelayedBodyContent()
            });
        }
    }

    private sealed class DelayedBodyContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
