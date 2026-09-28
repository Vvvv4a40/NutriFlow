using System.Net;
using System.Net.Http.Json;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;

namespace NutriFlow.Api.Tests;

public sealed class MealSessionConcurrencyApiTests
{
    [Fact]
    public async Task AddMessage_WhenCalculatedPreviewIsUnchanged_ChangesPreviewToken()
    {
        await using TestApiFactory factory = new TestApiFactory(
            parser: new UnchangedDraftParser());
        using HttpClient client = factory.CreateClient();
        MealSessionResponse initial = await CreateSessionAsync(client);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{initial.Id}/messages",
            new AddMealSessionMessageRequest("Добавил немного специй."));
        MealSessionResponse updated = await ReadSessionAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, updated.Messages.Count);
        Assert.Equal("Добавил немного специй.", updated.Messages[1]);
        Assert.Equal(
            Assert.Single(initial.Dishes).TotalNutrition,
            Assert.Single(updated.Dishes).TotalNutrition);
        Assert.NotEqual(initial.PreviewToken, updated.PreviewToken);

        using HttpResponseMessage confirmationResponse = await client.PostAsJsonAsync(
            $"/api/meal-sessions/{initial.Id}/confirm",
            new ConfirmMealSessionRequest(initial.PreviewToken));
        ConfirmMealSessionResponse confirmation = await confirmationResponse.Content
            .ReadFromJsonAsync<ConfirmMealSessionResponse>() ?? throw new InvalidDataException();

        Assert.Equal(HttpStatusCode.Conflict, confirmationResponse.StatusCode);
        Assert.Equal("StalePreview", confirmation.Outcome);
        Assert.Equal(updated.PreviewToken, confirmation.Session.PreviewToken);
        Assert.Empty(confirmation.Entries);
    }

    [Fact]
    public async Task AddMessage_WhenRequestsRaceWithUnchangedPreview_RejectsLostUpdate()
    {
        UnchangedDraftParser parser = new UnchangedDraftParser(
            synchronizeAddedMessages: true);
        await using TestApiFactory factory = new TestApiFactory(parser: parser);
        using HttpClient client = factory.CreateClient();
        MealSessionResponse initial = await CreateSessionAsync(client);
        string[] additions =
        [
            "Добавил немного специй.",
            "Готовил на слабом огне."
        ];

        Task<HttpResponseMessage>[] requests = additions
            .Select(message => client.PostAsJsonAsync(
                $"/api/meal-sessions/{initial.Id}/messages",
                new AddMealSessionMessageRequest(message)))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(requests)
            .WaitAsync(TimeSpan.FromSeconds(20));

        try
        {
            Assert.Equal(2, parser.SynchronizedRequestCount);
            HttpResponseMessage successfulResponse = Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(
                responses,
                response => response.StatusCode == HttpStatusCode.Conflict);
            MealSessionResponse successful = await ReadSessionAsync(successfulResponse);
            MealSessionResponse saved = await client.GetFromJsonAsync<MealSessionResponse>(
                $"/api/meal-sessions/{initial.Id}") ?? throw new InvalidDataException();

            Assert.Equal(2, saved.Messages.Count);
            Assert.Contains(saved.Messages[1], additions);
            Assert.Equal(successful.Messages, saved.Messages);
            Assert.Equal(successful.PreviewToken, saved.PreviewToken);
            Assert.NotEqual(initial.PreviewToken, saved.PreviewToken);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    private static async Task<MealSessionResponse> CreateSessionAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/meal-sessions",
            new CreateMealSessionRequest(
                ["Готовлю блюдо."],
                new DateOnly(2026, 9, 28)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadSessionAsync(response);
    }

    private static async Task<MealSessionResponse> ReadSessionAsync(
        HttpResponseMessage response)
    {
        return await response.Content.ReadFromJsonAsync<MealSessionResponse>() ??
               throw new InvalidDataException();
    }

    private sealed class UnchangedDraftParser : IMealParser
    {
        private readonly bool _synchronizeAddedMessages;
        private readonly TaskCompletionSource _bothRequestsArrived = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _synchronizedRequestCount;

        public UnchangedDraftParser(bool synchronizeAddedMessages = false)
        {
            _synchronizeAddedMessages = synchronizeAddedMessages;
        }

        public int SynchronizedRequestCount => Volatile.Read(ref _synchronizedRequestCount);

        public async Task<MealDraft> ParseAsync(
            CaptureSession session,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_synchronizeAddedMessages && session.InputEvents.Count > 1)
            {
                if (Interlocked.Increment(ref _synchronizedRequestCount) == 2)
                {
                    _bothRequestsArrived.TrySetResult();
                }

                await _bothRequestsArrived.Task.WaitAsync(
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
            }

            return new MealDraft(
                [
                    new DishDraft(
                        "Блюдо",
                        [new IngredientDraft("Демо-продукт A", 100m)],
                        100m,
                        [50m])
                ],
                []);
        }
    }
}
