using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Api.Tests;

public sealed class MealWorkflowQueryTests
{
    [Fact]
    public Task Preview_ResolvesRepeatedProductOnceButReloadsItForNextRequest()
    {
        return VerifyPreviewAsync();
    }

    [Fact]
    public async Task Preview_WhenIndependentHostsStartTogether_KeepsQueriesAndDataIsolated()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(VerifyPreviewAsync)));
    }

    private static async Task VerifyPreviewAsync()
    {
        ProductQueryCounter counter = new ProductQueryCounter();
        await using TestApiFactory baseFactory = new TestApiFactory(parser: new RepeatedProductParser());
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddDbContext<NutriFlowDbContext>(options => options.AddInterceptors(counter))));
        using HttpClient client = factory.CreateClient();
        counter.Reset();

        using HttpResponseMessage create = await client.PostAsJsonAsync(
            "/api/meal-sessions",
            new CreateMealSessionRequest(["Готовлю два блюда"], new DateOnly(2026, 10, 5)));
        await ApiTestAssertions.AssertStatusAsync(factory, create, HttpStatusCode.Created);
        MealSessionResponse first = await create.Content.ReadFromJsonAsync<MealSessionResponse>()
            ?? throw new InvalidDataException();
        Assert.Equal(1, counter.Count);
        Assert.Equal(2, first.Dishes.Count);
        Assert.All(first.Dishes, dish => Assert.Equal(100m, dish.TotalNutrition?.Calories));

        using HttpResponseMessage save = await client.PostAsJsonAsync(
            "/api/products/manual",
            new CreateManualProductRequest("Демо-продукт A", 110m, 10m, 4m, 6m, false));
        await ApiTestAssertions.AssertStatusAsync(factory, save, HttpStatusCode.Created);
        counter.Reset();

        using HttpResponseMessage get = await client.GetAsync($"/api/meal-sessions/{first.Id}");
        await ApiTestAssertions.AssertStatusAsync(factory, get, HttpStatusCode.OK);
        MealSessionResponse second = await get.Content.ReadFromJsonAsync<MealSessionResponse>()
            ?? throw new InvalidDataException();
        Assert.Equal(1, counter.Count);
        Assert.NotEqual(first.PreviewToken, second.PreviewToken);
        Assert.All(second.Dishes, dish => Assert.Equal(110m, dish.TotalNutrition?.Calories));
    }

    private sealed class RepeatedProductParser : IMealParser
    {
        public Task<MealDraft> ParseAsync(
            CaptureSession session,
            CancellationToken cancellationToken = default)
        {
            MealDraft draft = new MealDraft(
                [
                    new DishDraft("Первое блюдо",
                        [new IngredientDraft("Демо-продукт A", 50m),
                         new IngredientDraft("демо-продукт a", 50m)], 100m, [100m]),
                    new DishDraft("Второе блюдо",
                        [new IngredientDraft("  Демо-продукт A  ", 100m)], 100m, [100m])
                ], []);
            return Task.FromResult(draft);
        }
    }

    private sealed class ProductQueryCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Products\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _count);
            }

            return ValueTask.FromResult(result);
        }
    }
}
