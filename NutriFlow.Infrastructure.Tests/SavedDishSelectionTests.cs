using Microsoft.EntityFrameworkCore;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Persistence;

namespace NutriFlow.Infrastructure.Tests;

public sealed class SavedDishSelectionTests
{
    [Fact]
    public async Task FindByNameAsync_PersistedIdHandleIsScopedToOwnerAndSurvivesRestart()
    {
        await using TestDatabase database = new();
        await database.MigrateAsync();
        Guid firstOwner = Guid.NewGuid();
        Guid secondOwner = Guid.NewGuid();
        const string previewToken = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        StoredSavedDish saved;
        await using (NutriFlowDbContext context = database.CreateContext())
        {
            foreach (Guid owner in new[] { firstOwner, secondOwner })
            {
                await context.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Users" ("Id", "CreatedAtUtc", "IsLegacyLocal")
                    VALUES ({owner}, {DateTimeOffset.UtcNow}, {false})
                    """);
            }

            MealDraft draft = new([new DishDraft("Моё блюдо", [new IngredientDraft("Молоко", 100m)],
                200m, DataQuality.Exact, [])], []);
            StoredMealSession session = await new MealSessionStore(context, firstOwner).CreateAsync(
                ["Сохранить блюдо"], draft, "{}", previewToken, MealSessionStatus.ReadyForConfirmation,
                new DateOnly(2026, 10, 9), purpose: MealSessionPurpose.CreateDish);
            SavedDishStore store = new(context, firstOwner);
            Assert.Equal(MealSessionConfirmationResult.Confirmed, await store.ConfirmSessionAsync(
                session.Id, previewToken, "Моё блюдо", 200m, new NutritionValues(50.125m, 3m, 4m, 5m), DataQuality.Exact));
            saved = (await store.FindBySessionIdAsync(session.Id))!;
        }

        await using NutriFlowDbContext reopened = database.CreateContext();
        SavedDishStore first = new(reopened, firstOwner);
        SavedDishStore second = new(reopened, secondOwner);
        string handle = saved.Product.Source.Reference!;
        StoredSavedDish restored = Assert.IsType<StoredSavedDish>(await first.FindByNameAsync(handle));
        Assert.Equal(saved.Id, restored.Id);
        Assert.Equal(50.125m, restored.Product.NutritionPer100Grams.Calories);
        Assert.Null(await second.FindByNameAsync(handle));
        Assert.Null(await first.FindByNameAsync($"saved-dish:{Guid.NewGuid():N}"));
        Assert.Equal(saved.Id, (await first.FindByNameAsync("Моё блюдо"))!.Id);
    }
}
