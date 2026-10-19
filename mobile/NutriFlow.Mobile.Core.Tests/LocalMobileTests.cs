using System.Net;
using System.Text;
using System.Text.Json;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;

namespace NutriFlow.Mobile.Core.Tests;

public sealed class LocalMobileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nutriflow-mobile-tests", Guid.NewGuid().ToString("N"));
    private readonly DateOnly _date = new(2026, 10, 9);

    [Fact]
    public async Task ProfilesAndSelectionSurviveRestart()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile first = await profiles.CreateAsync("Первый");
        LocalProfile second = await profiles.CreateAsync("Второй");
        await profiles.SelectAsync(first.Id);

        LocalProfileStore restarted = new(_root);
        Assert.Equal(first, await restarted.GetSelectedAsync());
        Assert.Equal(new[] { first, second }, await restarted.ListAsync());
        Assert.NotEqual(first.Id, second.Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    public async Task InvalidProfileNamesAreRejected(string name)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new LocalProfileStore(_root).CreateAsync(name));
        Assert.False(File.Exists(Path.Combine(_root, "profiles.json")));
    }

    [Fact]
    public async Task DuplicateNamesAreRejectedWithoutChangingSelection()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile first = await profiles.CreateAsync("Профиль");
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.CreateAsync(" профиль "));
        Assert.Equal(first, await profiles.GetSelectedAsync());
        Assert.Single(await profiles.ListAsync());
    }

    [Fact]
    public async Task CorruptProfileCatalogIsNotSilentlyReplaced()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "profiles.json");
        await File.WriteAllTextAsync(path, "not-json");
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalProfileStore(_root).CreateAsync("Новый"));
        Assert.Equal("not-json", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task UnknownProfileCannotOpenOrCreateADatabase()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile known = await profiles.CreateAsync("Известный");
        Guid unknown = Guid.NewGuid();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new LocalMealClient(profiles).GetDailyProgressAsync(unknown, _date));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => profiles.SelectAsync(unknown));
        Assert.Equal(known, await profiles.GetSelectedAsync());
        Assert.False(Directory.Exists(Path.Combine(_root, "profiles", unknown.ToString("N"))));
    }

    [Fact]
    public async Task ManualMealRequiresConfirmationAndRetriesDoNotDuplicateDiary()
    {
        (LocalProfileStore profiles, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        await client.AddProductAsync(profile.Id, Product("Продукт"));
        MealSessionResponse preview = await client.CreateManualMealAsync(profile.Id, "Продукт", 150m, _date);
        Assert.True(preview.CanConfirm);
        Assert.Empty((await client.GetDailyProgressAsync(profile.Id, _date)).Entries);
        ConfirmMealSessionResponse? confirmed = await client.ConfirmAsync(profile.Id, preview.Id, preview.PreviewToken);
        Assert.Equal("Confirmed", confirmed!.Outcome);
        Assert.Equal("AlreadyConfirmed", (await client.ConfirmAsync(profile.Id, preview.Id, preview.PreviewToken))!.Outcome);

        LocalMealClient restarted = new(new LocalProfileStore(_root));
        DailyProgressResponse progress = await restarted.GetDailyProgressAsync(profile.Id, _date);
        Assert.Single(progress.Entries);
        Assert.Equal(150m, progress.Consumed.Calories);
        Assert.Equal(15m, progress.Consumed.ProteinGrams);
        Assert.Equal(profile, await profiles.GetSelectedAsync());
    }

    [Fact]
    public async Task ProfilesHaveSeparateProductsGoalsAndDiary()
    {
        (LocalProfileStore profiles, LocalProfile first, LocalMealClient client) = await CreateClientAsync();
        LocalProfile second = await profiles.CreateAsync("Другой");
        await client.AddProductAsync(first.Id, Product("Одинаковое имя", 100m));
        await client.AddProductAsync(second.Id, Product("Одинаковое имя", 200m));
        await client.SetDailyGoalAsync(first.Id, _date, new NutritionResponse(2000m, 100m, 60m, 240m));
        MealSessionResponse draft = await client.CreateManualMealAsync(first.Id, "Одинаковое имя", 200m, _date);
        await client.ConfirmAsync(first.Id, draft.Id, draft.PreviewToken);

        Assert.Equal(100m, Assert.Single(await client.SearchProductsAsync(first.Id)).Calories);
        Assert.Equal(200m, Assert.Single(await client.SearchProductsAsync(second.Id)).Calories);
        Assert.NotNull((await client.GetDailyProgressAsync(first.Id, _date)).Goal);
        DailyProgressResponse other = await client.GetDailyProgressAsync(second.Id, _date);
        Assert.Null(other.Goal);
        Assert.Empty(other.Entries);
        Assert.Null(await client.FindSessionAsync(second.Id, draft.Id));
        Assert.Null(await client.ConfirmAsync(second.Id, draft.Id, draft.PreviewToken));
    }

    [Fact]
    public async Task MissingProductBlocksConfirmationUntilNutritionIsEntered()
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        MealSessionResponse draft = await client.CreateManualMealAsync(profile.Id, "Новый продукт", 100m, _date);
        Assert.False(draft.CanConfirm);
        Assert.NotEmpty(draft.Issues);
        Assert.Equal("NotReady", (await client.ConfirmAsync(profile.Id, draft.Id, draft.PreviewToken))!.Outcome);
        await client.AddProductAsync(profile.Id, Product("Новый продукт"));
        MealSessionResponse refreshed = (await client.FindSessionAsync(profile.Id, draft.Id))!;
        Assert.True(refreshed.CanConfirm);
        await client.ConfirmAsync(profile.Id, refreshed.Id, refreshed.PreviewToken);
        Assert.Single((await client.GetDailyProgressAsync(profile.Id, _date)).Entries);
    }

    [Fact]
    public async Task DishIsSavedSeparatelyAndPortionUsesDeterministicNutrition()
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        await client.AddProductAsync(profile.Id, Product("Ингредиент", 100m));
        MealSessionResponse dish = await client.CreateManualDishAsync(profile.Id, "Моё блюдо",
            [new IngredientDraft("Ингредиент", 600m, DataQuality.Estimated, 100m, DataQuality.Exact)], 1000m, _date);
        Assert.True(dish.CanConfirm);
        ConfirmMealSessionResponse result = (await client.ConfirmAsync(profile.Id, dish.Id, dish.PreviewToken))!;
        Assert.Equal(50m, result.SavedDish!.NutritionPer100Grams.Calories);
        Assert.Empty((await client.GetDailyProgressAsync(profile.Id, _date)).Entries);
        Assert.Single(await client.GetSavedDishesAsync(profile.Id));
        MealSessionResponse portion = await client.CreateSavedDishMealAsync(profile.Id, result.SavedDish.Id, 250m, _date);
        Assert.True(portion.CanConfirm);
        await client.ConfirmAsync(profile.Id, portion.Id, portion.PreviewToken);
        DailyProgressResponse progress = await client.GetDailyProgressAsync(profile.Id, _date);
        Assert.Equal(125m, progress.Consumed.Calories);
        Assert.Equal("Estimated", Assert.Single(progress.Entries).Quality);
    }

    [Fact]
    public async Task MixedQualityManualDishKeepsCompositionAndRemovedWeightsAcrossRestart()
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        ProductResponse exact = new("Synthetic exact ingredient", 100m, 0m, 0m, 25m,
            "ManualInput", "Exact", "Exact test source", null, null);
        ProductResponse estimated = new("Synthetic estimated ingredient", 100m, 0m, 0m, 25m,
            "ManualInput", "Estimated", "Estimated test source", null, null);
        await client.AddProductAsync(profile.Id, exact);
        await client.AddProductAsync(profile.Id, estimated);
        MealSessionResponse draft = await client.CreateManualDishAsync(profile.Id, "Synthetic mixed dish",
            new ManualIngredient[] { new(exact, 300m, RemovedWeightInGrams: 100m), new(estimated, 100m) },
            400m, _date);
        Assert.Equal("CreateDish", draft.Purpose);
        Assert.True(draft.CanConfirm);
        DishPreviewResponse preview = Assert.Single(draft.Dishes);
        Assert.Equal(400m, preview.FinalWeightInGrams);
        Assert.Equal("Exact", preview.FinalWeightQuality);
        Assert.Equal(new NutritionResponse(300m, 0m, 0m, 75m), preview.TotalNutrition);
        Assert.Equal(new NutritionResponse(75m, 0m, 0m, 18.75m), preview.NutritionPer100Grams);
        Assert.Equal("Estimated", preview.TotalNutritionQuality);
        Assert.Equal("Estimated", preview.NutritionPer100GramsQuality);
        Assert.Equal(new[] { exact, estimated }, preview.Ingredients.Select(ingredient => ingredient.ResolvedProduct));
        Assert.Equal(new decimal?[] { 300m, 100m }, preview.Ingredients.Select(ingredient => ingredient.WeightInGrams));
        Assert.Equal(new decimal?[] { 100m, 0m }, preview.Ingredients.Select(ingredient => ingredient.RemovedWeightInGrams));
        Assert.Equal(new decimal?[] { 200m, 100m }, preview.Ingredients.Select(ingredient => ingredient.IncludedWeightInGrams));
        Assert.All(preview.Ingredients, ingredient =>
        {
            Assert.Equal("Exact", ingredient.WeightQuality);
            Assert.Equal("Exact", ingredient.RemovedWeightQuality);
        });
        Assert.Equal(new[] { new NutritionResponse(200m, 0m, 0m, 50m), new NutritionResponse(100m, 0m, 0m, 25m) },
            preview.Ingredients.Select(ingredient => ingredient.Nutrition));
        Assert.Empty(preview.Portions);
        Assert.Empty((await client.GetDailyProgressAsync(profile.Id, _date)).Entries);

        LocalMealClient restarted = new(new LocalProfileStore(_root));
        MealSessionResponse reopened = (await restarted.FindSessionAsync(profile.Id, draft.Id))!;
        Assert.Equal("CreateDish", reopened.Purpose);
        Assert.Equal(draft.PreviewToken, reopened.PreviewToken);
        ConfirmMealSessionResponse confirmed = (await restarted.ConfirmAsync(profile.Id, reopened.Id, reopened.PreviewToken))!;
        Assert.Equal("Confirmed", confirmed.Outcome);
        Assert.Empty(confirmed.Entries);
        SavedDishResponse saved = confirmed.SavedDish!;
        Assert.Equal(draft.Id, saved.SourceSessionId);
        Assert.Equal(preview.Name, saved.Name);
        Assert.Equal(400m, saved.FinalWeightInGrams);
        Assert.Equal(preview.NutritionPer100Grams, saved.NutritionPer100Grams);
        Assert.Equal("Estimated", saved.NutritionQuality);
        Assert.Empty((await restarted.GetDailyProgressAsync(profile.Id, _date)).Entries);

        LocalMealClient afterSaving = new(new LocalProfileStore(_root));
        SavedDishDetailResponse detail = (await afterSaving.FindSavedDishAsync(profile.Id, saved.Id))!;
        Assert.Equal(saved, Assert.Single(await afterSaving.GetSavedDishesAsync(profile.Id)));
        Assert.Equal(saved, detail.SavedDish);
        Assert.Equal(preview.Name, detail.Dish.Name);
        Assert.Equal(preview.FinalWeightInGrams, detail.Dish.FinalWeightInGrams);
        Assert.Equal(preview.FinalWeightQuality, detail.Dish.FinalWeightQuality);
        Assert.Equal(preview.TotalNutrition, detail.Dish.TotalNutrition);
        Assert.Equal(preview.TotalNutritionQuality, detail.Dish.TotalNutritionQuality);
        Assert.Equal(preview.NutritionPer100Grams, detail.Dish.NutritionPer100Grams);
        Assert.Equal(preview.NutritionPer100GramsQuality, detail.Dish.NutritionPer100GramsQuality);
        Assert.Equal(preview.Ingredients.ToArray(), detail.Dish.Ingredients.ToArray());
        Assert.Empty(detail.Dish.Portions);

        MealSessionResponse portion = await afterSaving.CreateSavedDishMealAsync(profile.Id, saved.Id, 200m, _date);
        Assert.Equal("Diary", portion.Purpose);
        Assert.True(portion.CanConfirm);
        DishPreviewResponse portionDish = Assert.Single(portion.Dishes);
        Assert.Equal("SavedDish", Assert.Single(portionDish.Ingredients).ResolvedProduct!.SourceKind);
        PortionPreviewResponse portionPreview = Assert.Single(portionDish.Portions);
        Assert.Equal(new NutritionResponse(150m, 0m, 0m, 37.5m), portionPreview.Nutrition);
        Assert.Equal("Estimated", portionPreview.NutritionQuality);
        Assert.Empty((await afterSaving.GetDailyProgressAsync(profile.Id, _date)).Entries);
        MealEntryResponse entry = Assert.Single((await afterSaving.ConfirmAsync(profile.Id, portion.Id, portion.PreviewToken))!.Entries);

        LocalMealClient afterEating = new(new LocalProfileStore(_root));
        DailyProgressResponse progress = await afterEating.GetDailyProgressAsync(profile.Id, _date);
        Assert.Equal(entry, Assert.Single(progress.Entries));
        Assert.Equal(200m, entry.WeightInGrams);
        Assert.Equal("Estimated", entry.Quality);
        Assert.Equal(new NutritionResponse(150m, 0m, 0m, 37.5m), entry.Nutrition);
        Assert.Equal(entry.Nutrition, progress.Consumed);
        ProductResponse[] products = (await afterEating.SearchProductsAsync(profile.Id)).ToArray();
        Assert.Equal(2, products.Length);
        Assert.Contains(exact, products);
        Assert.Contains(estimated, products);
    }

    [Fact]
    public async Task EntryEditsAreRevisionCheckedAndDeletionUpdatesTotals()
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        await client.AddProductAsync(profile.Id, Product("Продукт"));
        MealSessionResponse draft = await client.CreateManualMealAsync(profile.Id, "Продукт", 100m, _date);
        MealEntryResponse entry = Assert.Single((await client.ConfirmAsync(profile.Id, draft.Id, draft.PreviewToken))!.Entries);
        MealEntryResponse updated = await client.UpdateEntryAsync(profile.Id, entry.Id, entry.Revision, 200m);
        Assert.Equal(200m, updated.Nutrition.Calories);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.UpdateEntryAsync(profile.Id, entry.Id, entry.Revision, 300m));
        await client.DeleteEntryAsync(profile.Id, updated.Id, updated.Revision);
        Assert.Empty((await client.GetDailyProgressAsync(profile.Id, _date)).Entries);
        Assert.Equal(0m, (await client.GetDailyProgressAsync(profile.Id, _date)).Consumed.Calories);
    }

    [Fact]
    public async Task ExactPortionEditPreservesEstimatedProductAndDailyTotalsAfterRestart()
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        ProductResponse product = new("Synthetic nutrition", 100m, 0m, 0m, 25m,
            "ManualInput", "Estimated", "Synthetic test source", null, null);
        await client.AddProductAsync(profile.Id, product);
        MealSessionResponse draft = await client.CreateManualMealAsync(profile.Id, product, 125m, _date);
        Assert.Empty((await client.GetDailyProgressAsync(profile.Id, _date)).Entries);

        MealEntryResponse entry = Assert.Single((await client.ConfirmAsync(profile.Id, draft.Id, draft.PreviewToken))!.Entries);
        Assert.Equal(new NutritionResponse(125m, 0m, 0m, 31.25m), entry.Nutrition);
        Assert.Equal("Estimated", entry.Quality);
        MealEntryResponse updated = await client.UpdateEntryAsync(profile.Id, entry.Id, entry.Revision, 50m,
            DataQuality.Exact);

        LocalMealClient restarted = new(new LocalProfileStore(_root));
        DailyProgressResponse progress = await restarted.GetDailyProgressAsync(profile.Id, _date);
        MealEntryResponse persisted = Assert.Single(progress.Entries);
        Assert.Equal(updated, persisted);
        Assert.Equal(50m, persisted.WeightInGrams);
        Assert.Equal("Estimated", persisted.Quality);
        Assert.Equal(new NutritionResponse(50m, 0m, 0m, 12.5m), persisted.Nutrition);
        Assert.Equal(persisted.Nutrition, progress.Consumed);

        await restarted.DeleteEntryAsync(profile.Id, persisted.Id, persisted.Revision);
        LocalMealClient afterDeletion = new(new LocalProfileStore(_root));
        DailyProgressResponse empty = await afterDeletion.GetDailyProgressAsync(profile.Id, _date);
        Assert.Empty(empty.Entries);
        Assert.Equal(new NutritionResponse(0m, 0m, 0m, 0m), empty.Consumed);
        Assert.Equal(product, Assert.Single(await afterDeletion.SearchProductsAsync(profile.Id)));
    }

    [Fact]
    public async Task SavedDishPortionEditsAndDeletionLeaveStoredCompositionUnchangedAfterRestart()
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        ProductResponse product = new("Synthetic ingredient", 100m, 0m, 0m, 25m,
            "ManualInput", "Estimated", "Synthetic test source", null, null);
        await client.AddProductAsync(profile.Id, product);
        MealSessionResponse dish = await client.CreateManualDishAsync(profile.Id, "Synthetic dish",
            new ManualIngredient[] { new(product, 300m, RemovedWeightInGrams: 100m) }, 400m, _date);
        SavedDishResponse saved = (await client.ConfirmAsync(profile.Id, dish.Id, dish.PreviewToken))!.SavedDish!;
        SavedDishDetailResponse before = (await client.FindSavedDishAsync(profile.Id, saved.Id))!;
        Assert.Equal(saved, before.SavedDish);
        Assert.Equal(400m, saved.FinalWeightInGrams);
        Assert.Equal(new NutritionResponse(50m, 0m, 0m, 12.5m), saved.NutritionPer100Grams);
        IngredientPreviewResponse ingredient = Assert.Single(before.Dish.Ingredients);
        Assert.Equal(product, ingredient.ResolvedProduct);
        Assert.Equal(300m, ingredient.WeightInGrams);
        Assert.Equal(100m, ingredient.RemovedWeightInGrams);
        Assert.Equal(200m, ingredient.IncludedWeightInGrams);

        MealSessionResponse portion = await client.CreateSavedDishMealAsync(profile.Id, saved.Id, 250m, _date);
        Assert.Empty((await client.GetDailyProgressAsync(profile.Id, _date)).Entries);
        MealEntryResponse entry = Assert.Single((await client.ConfirmAsync(profile.Id, portion.Id, portion.PreviewToken))!.Entries);
        Assert.Equal(new NutritionResponse(125m, 0m, 0m, 31.25m), entry.Nutrition);
        MealEntryResponse updated = await client.UpdateEntryAsync(profile.Id, entry.Id, entry.Revision, 100m,
            DataQuality.Exact);

        LocalMealClient restarted = new(new LocalProfileStore(_root));
        DailyProgressResponse progress = await restarted.GetDailyProgressAsync(profile.Id, _date);
        MealEntryResponse persisted = Assert.Single(progress.Entries);
        Assert.Equal(updated, persisted);
        Assert.Equal(saved.Name, persisted.Name);
        Assert.Equal(100m, persisted.WeightInGrams);
        Assert.Equal("Estimated", persisted.Quality);
        Assert.Equal(new NutritionResponse(50m, 0m, 0m, 12.5m), persisted.Nutrition);
        Assert.Equal(persisted.Nutrition, progress.Consumed);
        SavedDishDetailResponse afterEdit = (await restarted.FindSavedDishAsync(profile.Id, saved.Id))!;

        await restarted.DeleteEntryAsync(profile.Id, persisted.Id, persisted.Revision);
        LocalMealClient afterDeletion = new(new LocalProfileStore(_root));
        DailyProgressResponse empty = await afterDeletion.GetDailyProgressAsync(profile.Id, _date);
        Assert.Empty(empty.Entries);
        Assert.Equal(new NutritionResponse(0m, 0m, 0m, 0m), empty.Consumed);
        Assert.Equal(saved, Assert.Single(await afterDeletion.GetSavedDishesAsync(profile.Id)));
        Assert.Equal(product, Assert.Single(await afterDeletion.SearchProductsAsync(profile.Id)));
        SavedDishDetailResponse afterDelete = (await afterDeletion.FindSavedDishAsync(profile.Id, saved.Id))!;

        foreach (SavedDishDetailResponse snapshot in new[] { afterEdit, afterDelete })
        {
            Assert.Equal(before.SavedDish, snapshot.SavedDish);
            Assert.Equal(before.Dish.Name, snapshot.Dish.Name);
            Assert.Equal(before.Dish.FinalWeightInGrams, snapshot.Dish.FinalWeightInGrams);
            Assert.Equal(before.Dish.FinalWeightQuality, snapshot.Dish.FinalWeightQuality);
            Assert.Equal(before.Dish.TotalNutrition, snapshot.Dish.TotalNutrition);
            Assert.Equal(before.Dish.TotalNutritionQuality, snapshot.Dish.TotalNutritionQuality);
            Assert.Equal(before.Dish.NutritionPer100Grams, snapshot.Dish.NutritionPer100Grams);
            Assert.Equal(before.Dish.NutritionPer100GramsQuality, snapshot.Dish.NutritionPer100GramsQuality);
            Assert.Equal(before.Dish.Ingredients.ToArray(), snapshot.Dish.Ingredients.ToArray());
            Assert.Equal(before.Dish.Portions.ToArray(), snapshot.Dish.Portions.ToArray());
        }
    }

    [Fact]
    public async Task ManualNutritionCannotClaimUnownedPhotoOrVerifiedSource()
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        ProductResponse manual = Product("Продукт");
        await Assert.ThrowsAsync<ArgumentException>(() => client.AddProductAsync(profile.Id, manual with { DataQuality = "Verified" }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.AddProductAsync(profile.Id, manual with
        {
            SourceKind = "LabelPhoto",
            SourceReference = $"label-photo:{Guid.NewGuid():N}.jpg"
        }));
        Assert.Empty(await client.SearchProductsAsync(profile.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task NonpositivePortionsAreRejected(int grams)
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.CreateManualMealAsync(profile.Id, "Продукт", grams, _date));
    }

    [Fact]
    public async Task OfflineOperationsDoNotCreateNetworkClients()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Офлайн");
        LocalMealClient client = new(profiles, () => throw new InvalidOperationException("Network must not be used"));
        await client.AddProductAsync(profile.Id, Product("Продукт"));
        await client.SetDailyGoalAsync(profile.Id, _date, new NutritionResponse(1500m, 80m, 50m, 180m));
        MealSessionResponse session = await client.CreateManualMealAsync(profile.Id, "Продукт", 100m, _date);
        await client.ConfirmAsync(profile.Id, session.Id, session.PreviewToken);
        Assert.Equal(100m, (await client.GetDailyProgressAsync(profile.Id, _date)).Consumed.Calories);
    }

    [Fact]
    public async Task NetworkResultKeepsCapturedProfileAfterSelectionChanges()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile first = await profiles.CreateAsync("Первый");
        LocalProfile second = await profiles.CreateAsync("Второй");
        await profiles.SelectAsync(first.Id);
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        LocalMealClient client = new(profiles, () => new HttpClient(new Handler(async request =>
        {
            Assert.Equal("api.groq.com", request.RequestUri!.Host);
            Assert.Equal("synthetic-test-key", request.Headers.Authorization!.Parameter);
            ready.TrySetResult();
            await release.Task;
            return GroqDraftResponse();
        })));
        await client.AddProductAsync(first.Id, Product("Продукт"));
        Task<MealSessionResponse> pending = client.CreateSessionAsync(first.Id, ["Съел продукт"], _date,
            MealSessionPurpose.Diary, new GroqSettings("synthetic-test-key"));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await profiles.SelectAsync(second.Id);
        release.TrySetResult();
        MealSessionResponse session = await pending;
        Assert.NotNull(await client.FindSessionAsync(first.Id, session.Id));
        Assert.Null(await client.FindSessionAsync(second.Id, session.Id));
        Assert.Empty((await client.GetDailyProgressAsync(second.Id, _date)).Entries);
    }

    [Fact]
    public async Task MissingKeyDoesNotSilentlyUseFakeAI()
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateSessionAsync(profile.Id, ["Продукт"], _date,
            MealSessionPurpose.Diary, new GroqSettings("")));
        Assert.Empty((await client.GetDailyProgressAsync(profile.Id, _date)).Entries);
        Assert.DoesNotContain("private-test-value", new GroqSettings("private-test-value").ToString());
    }

    [Fact]
    public async Task ExplicitProductSelectionKeepsEstimatedNamesakeAfterRestartAndConfirmation()
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        ProductResponse exact = Product("Одинаковое имя", 100m);
        ProductResponse selected = Product("Одинаковое имя", 200.125m) with { DataQuality = "Estimated" };
        await client.AddProductAsync(profile.Id, exact);
        await client.AddProductAsync(profile.Id, selected);

        MealSessionResponse session = await client.CreateManualMealAsync(profile.Id, selected, 150m, _date);
        Assert.True(session.CanConfirm);
        Assert.Equal("Одинаковое имя", Assert.Single(session.Dishes).Name);
        ProductResponse resolved = Assert.Single(session.Dishes.Single().Ingredients).ResolvedProduct!;
        Assert.Equal(200.125m, resolved.Calories);
        Assert.Equal("Estimated", resolved.DataQuality);

        LocalMealClient restarted = new(new LocalProfileStore(_root));
        MealSessionResponse reopened = (await restarted.FindSessionAsync(profile.Id, session.Id))!;
        Assert.Equal(session.PreviewToken, reopened.PreviewToken);
        ConfirmMealSessionResponse result = (await restarted.ConfirmAsync(profile.Id, session.Id, session.PreviewToken))!;
        Assert.Equal("Confirmed", result.Outcome);
        MealEntryResponse entry = Assert.Single(result.Entries);
        Assert.Equal(300.1875m, entry.Nutrition.Calories);
        Assert.Equal("Estimated", entry.Quality);
    }

    [Fact]
    public async Task ExplicitProductSelectionResolvesSameQualityDifferentNutrition()
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        await client.AddProductAsync(profile.Id, Product("Яблоко", 100m));
        ProductResponse selected = Product("Яблоко", 200m);
        await client.AddProductAsync(profile.Id, selected);

        MealSessionResponse session = await client.CreateManualMealAsync(profile.Id, selected, 100m, _date);

        Assert.True(session.CanConfirm);
        Assert.Equal(200m, Assert.Single((await client.ConfirmAsync(profile.Id, session.Id, session.PreviewToken))!.Entries)
            .Nutrition.Calories);
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateManualMealAsync(profile.Id, "Яблоко", 100m, _date));
    }

    [Fact]
    public async Task ExplicitManualDishUsesEachSelectedNamesakeAndDeterministicRemovedWeights()
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        ProductResponse first = Product("Молоко", 100m);
        ProductResponse second = Product("Молоко", 200m);
        await client.AddProductAsync(profile.Id, first);
        await client.AddProductAsync(profile.Id, second);

        MealSessionResponse session = await client.CreateManualDishAsync(profile.Id, "Смесь",
            new ManualIngredient[] { new(first, 100m), new(second, 100m, RemovedWeightInGrams: 50m) }, 200m, _date);

        Assert.True(session.CanConfirm);
        Assert.Equal(new[] { 100m, 200m }, session.Dishes.Single().Ingredients.Select(item => item.ResolvedProduct!.Calories));
        SavedDishResponse saved = (await client.ConfirmAsync(profile.Id, session.Id, session.PreviewToken))!.SavedDish!;
        Assert.Equal(100m, saved.NutritionPer100Grams.Calories);
    }

    [Fact]
    public async Task SavedDishSelectionByIdIgnoresCatalogNameCollisionAfterRestart()
    {
        (LocalProfileStore profiles, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        ProductResponse ingredient = Product("Ингредиент", 100m);
        await client.AddProductAsync(profile.Id, ingredient);
        MealSessionResponse dish = await client.CreateManualDishAsync(profile.Id, "Сохранённое блюдо",
            new ManualIngredient[] { new(ingredient, 100m) }, 200m, _date);
        SavedDishResponse saved = (await client.ConfirmAsync(profile.Id, dish.Id, dish.PreviewToken))!.SavedDish!;
        await client.AddProductAsync(profile.Id, Product(saved.Name, 800m));

        MealSessionResponse portion = await client.CreateSavedDishMealAsync(profile.Id, saved.Id, 250m, _date);
        Assert.True(portion.CanConfirm);
        Assert.Equal(saved.Name, portion.Dishes.Single().Name);
        Assert.Equal("SavedDish", portion.Dishes.Single().Ingredients.Single().ResolvedProduct!.SourceKind);
        LocalMealClient restarted = new(new LocalProfileStore(_root));
        Assert.Equal(portion.PreviewToken, (await restarted.FindSessionAsync(profile.Id, portion.Id))!.PreviewToken);
        ConfirmMealSessionResponse result = (await restarted.ConfirmAsync(profile.Id, portion.Id, portion.PreviewToken))!;
        Assert.Equal("Confirmed", result.Outcome);
        Assert.Equal(125m, Assert.Single(result.Entries).Nutrition.Calories);

        LocalProfile other = await profiles.CreateAsync("Другой профиль");
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            client.CreateSavedDishMealAsync(other.Id, saved.Id, 250m, _date));
    }

    [Fact]
    public async Task ExplicitProductSelectionRejectsForeignOrChangedProduct()
    {
        (LocalProfileStore profiles, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        ProductResponse selected = Product("Личный продукт", 100m);
        await client.AddProductAsync(profile.Id, selected);
        LocalProfile other = await profiles.CreateAsync("Другой профиль");

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            client.CreateManualMealAsync(other.Id, selected, 100m, _date));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            client.CreateManualMealAsync(profile.Id, selected with { Calories = 200m }, 100m, _date));
        Assert.Empty((await client.GetDailyProgressAsync(profile.Id, _date)).Entries);
        Assert.Empty((await client.GetDailyProgressAsync(other.Id, _date)).Entries);
    }

    [Fact]
    public async Task ManualDraftCorrectionSendsOriginalCompositionBeforeCorrectionWithoutAliasesOrKey()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Ручной ввод");
        string? requestBody = null;
        LocalMealClient client = new(profiles, () => new HttpClient(new Handler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            Assert.Equal("private-test-key", request.Headers.Authorization!.Parameter);
            return GroqDraftResponse();
        })));
        ProductResponse product = Product("Продукт", 100m);
        await client.AddProductAsync(profile.Id, product);
        MealSessionResponse session = await client.CreateManualDishAsync(profile.Id, "Ручное блюдо",
            new ManualIngredient[] { new(product, 600.125m, DataQuality.Estimated, 100.375m) },
            1000.625m, _date);

        string initial = string.Join("\n", session.Messages);
        Assert.Contains("Ручное блюдо", initial);
        Assert.Contains("CreateDish", initial);
        Assert.Contains("исходный вес 600.125 г (примерно)", initial);
        Assert.Contains("убрано 100.375 г (точно)", initial);
        Assert.Contains("Готовый вес блюда: 1000.625 г (точно)", initial);
        Assert.DoesNotContain("selected-", initial);
        const string correction = "Уточнение: готовый вес 900.25 г.";
        await client.AddMessageAsync(profile.Id, session.Id, correction, new GroqSettings("private-test-key"));

        using JsonDocument sent = JsonDocument.Parse(requestBody!);
        string prompt = sent.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("Продукт", prompt);
        Assert.Contains("600.125", prompt);
        Assert.Contains("100.375", prompt);
        Assert.Contains("1000.625", prompt);
        Assert.Contains(correction, prompt);
        Assert.True(prompt.IndexOf("600.125", StringComparison.Ordinal) < prompt.IndexOf(correction, StringComparison.Ordinal));
        Assert.DoesNotContain("selected-", prompt);
        Assert.DoesNotContain("private-test-key", requestBody!);
    }

    [Fact]
    public async Task SavedDishPortionHistoryContainsCanonicalNameAndExactOriginalPortion()
    {
        (_, LocalProfile profile, LocalMealClient client) = await CreateClientAsync();
        ProductResponse ingredient = Product("Ингредиент", 100m);
        await client.AddProductAsync(profile.Id, ingredient);
        MealSessionResponse dish = await client.CreateManualDishAsync(profile.Id, "Моё рагу",
            new ManualIngredient[] { new(ingredient, 100m) }, 200m, _date);
        SavedDishResponse saved = (await client.ConfirmAsync(profile.Id, dish.Id, dish.PreviewToken))!.SavedDish!;

        MealSessionResponse portion = await client.CreateSavedDishMealAsync(profile.Id, saved.Id, 250.875m, _date,
            DataQuality.Estimated);

        string initial = string.Join("\n", portion.Messages);
        Assert.Contains("Моё рагу", initial);
        Assert.Contains("Diary", initial);
        Assert.Contains("исходный вес 250.875 г (примерно)", initial);
        Assert.Contains("Готовый вес блюда: 250.875 г (примерно)", initial);
        Assert.Contains("Съеденная порция: 250.875 г (примерно)", initial);
        Assert.DoesNotContain("saved-dish:", initial);
    }

    [Fact]
    public async Task ManualNamesakeCorrectionIsRejectedBeforeNetworkWithoutChangingHistoryOrNutrition()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Ручной выбор");
        int networkClients = 0;
        LocalMealClient client = new(profiles, () =>
        {
            networkClients++;
            throw new InvalidOperationException("Network must not be used");
        });
        await client.AddProductAsync(profile.Id, Product("Одинаковое имя", 100m));
        ProductResponse selected = Product("Одинаковое имя", 200m) with { DataQuality = "Estimated" };
        await client.AddProductAsync(profile.Id, selected);
        MealSessionResponse before = await client.CreateManualMealAsync(profile.Id, selected, 100m, _date);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.AddMessageAsync(profile.Id, before.Id, "Съел 150 г", new GroqSettings("synthetic-test-key")));

        Assert.Contains("несколько вариантов", error.Message);
        Assert.Equal(0, networkClients);
        MealSessionResponse after = (await client.FindSessionAsync(profile.Id, before.Id))!;
        Assert.Equal(before.Messages, after.Messages);
        Assert.Equal(before.PreviewToken, after.PreviewToken);
        Assert.Equal(200m, after.Dishes.Single().Ingredients.Single().ResolvedProduct!.Calories);
        Assert.Empty((await client.GetDailyProgressAsync(profile.Id, _date)).Entries);
    }

    [Fact]
    public async Task SavedDishNamesakeCorrectionIsRejectedBeforeNetworkWithoutChangingHistory()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Моё блюдо");
        int networkClients = 0;
        LocalMealClient client = new(profiles, () =>
        {
            networkClients++;
            throw new InvalidOperationException("Network must not be used");
        });
        ProductResponse ingredient = Product("Ингредиент", 100m);
        await client.AddProductAsync(profile.Id, ingredient);
        MealSessionResponse dish = await client.CreateManualDishAsync(profile.Id, "Рагу",
            new ManualIngredient[] { new(ingredient, 100m) }, 200m, _date);
        SavedDishResponse saved = (await client.ConfirmAsync(profile.Id, dish.Id, dish.PreviewToken))!.SavedDish!;
        await client.AddProductAsync(profile.Id, Product("Рагу", 800m));
        MealSessionResponse before = await client.CreateSavedDishMealAsync(profile.Id, saved.Id, 100m, _date);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.AddMessageAsync(profile.Id, before.Id, "Съел 150 г", new GroqSettings("synthetic-test-key")));

        Assert.Contains("несколько вариантов", error.Message);
        Assert.Equal(0, networkClients);
        MealSessionResponse after = (await client.FindSessionAsync(profile.Id, before.Id))!;
        Assert.Equal(before.Messages, after.Messages);
        Assert.Equal(before.PreviewToken, after.PreviewToken);
        Assert.Equal(50m, after.Dishes.Single().Ingredients.Single().ResolvedProduct!.Calories);
        Assert.Empty((await client.GetDailyProgressAsync(profile.Id, _date)).Entries);
    }

    private async Task<(LocalProfileStore Profiles, LocalProfile Profile, LocalMealClient Client)> CreateClientAsync()
    {
        LocalProfileStore profiles = new(_root);
        LocalProfile profile = await profiles.CreateAsync("Мой профиль");
        return (profiles, profile, new LocalMealClient(profiles));
    }

    private static ProductResponse Product(string name, decimal calories = 100m) =>
        new(name, calories, 10m, 5m, 15m, "ManualInput", "Exact", "Ручной ввод", null, null);

    private static HttpResponseMessage GroqDraftResponse()
    {
        string draft = """
            {"sessionState":"ReadyForReview","dishes":[{"name":"Продукт","ingredients":[{"productName":"Продукт","weightInGrams":100,"weightQuality":"Exact","removedWeightInGrams":0,"removedWeightQuality":"Exact"}],"finalWeightInGrams":100,"finalWeightQuality":"Exact","portions":[{"weightInGrams":100,"fractionOfDish":null,"weightQuality":"Exact"}]}],"clarificationQuestions":[]}
            """;
        string body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = draft }, finish_reason = "stop" } } });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
}
