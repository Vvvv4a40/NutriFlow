using System.Net;
using System.Net.Http.Headers;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Ai;
using NutriFlow.Web.Diagnostics;

namespace NutriFlow.Web.Tests;

public sealed class FakeGroqHandlerTests
{
    [Fact]
    public async Task FakeKeyParsesWeighedItemsLikeGroq()
    {
        MealDraft draft = await ParseAsync(MealSessionPurpose.Diary, "гречка 150 г, куриная грудка 120 г и огурец 80,5 г");

        Assert.Equal(["гречка", "куриная грудка", "огурец"], draft.Dishes.Select(dish => dish.Name));
        Assert.Equal([150m, 120m, 80.5m], draft.Dishes.Select(dish => dish.Portions.Single().WeightInGrams));
        Assert.Empty(draft.ClarificationQuestions);
    }

    [Fact]
    public async Task MissingWeightBecomesAQuestionAndALaterAnswerFillsIt()
    {
        MealDraft asked = await ParseAsync(MealSessionPurpose.Diary, "гречка, куриная грудка 120 г");
        MealDraft answered = await ParseAsync(MealSessionPurpose.Diary, "гречка, куриная грудка 120 г", "150 г");

        Assert.Equal(["Сколько граммов «гречка»?"], asked.ClarificationQuestions);
        Assert.Null(asked.Dishes[0].Ingredients[0].WeightInGrams);
        Assert.Empty(answered.ClarificationQuestions);
        Assert.Equal(150m, answered.Dishes[0].Ingredients[0].WeightInGrams);
    }

    [Fact]
    public async Task DishHasItsFinalWeightAndNoPortions()
    {
        MealDraft draft = await ParseAsync(MealSessionPurpose.CreateDish, "фарш 600 г, лук 150 г. Готовое блюдо 700 г");

        DishDraft dish = Assert.Single(draft.Dishes);
        Assert.Equal(700m, dish.FinalWeightInGrams);
        Assert.Equal(["фарш", "лук"], dish.Ingredients.Select(ingredient => ingredient.ProductName));
        Assert.Empty(dish.Portions);
    }

    [Fact]
    public async Task OtherKeysGoToTheRealService()
    {
        RecordingHandler inner = new();
        using HttpClient client = Client(inner, "gsk_real");

        using HttpResponseMessage response = await client.PostAsync("chat/completions", new StringContent("{}"));

        Assert.Equal(1, inner.Requests);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<MealDraft> ParseAsync(MealSessionPurpose purpose, params string[] messages)
    {
        RecordingHandler inner = new();
        using HttpClient client = Client(inner, FakeGroqHandler.Key);
        CaptureSession session = new(purpose);
        foreach (string message in messages)
        {
            session.AddEvent(new InputEvent(message));
        }

        session.FinishCollecting();
        MealDraft draft = await new GroqMealParser(client, "model").ParseAsync(session);
        Assert.Equal(0, inner.Requests);
        return draft;
    }

    private static HttpClient Client(HttpMessageHandler inner, string key)
    {
        HttpClient client = new(new FakeGroqHandler(inner)) { BaseAddress = new Uri("https://api.groq.com/openai/v1/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }
}
