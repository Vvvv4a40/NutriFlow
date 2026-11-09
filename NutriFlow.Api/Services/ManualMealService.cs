using NutriFlow.Domain;
using NutriFlow.Infrastructure;

namespace NutriFlow.Api.Services;

// calculations entered by hand: a product or a saved dish picked from the lists, or a dish put together from products.
// The client sends only the id of what was picked, its nutrition comes from the catalog
public sealed class ManualMealService(
    LocalProductCatalog catalog,
    MealSessionStore sessions,
    DailyDiaryStore diary,
    SavedDishStore dishes)
{
    public const int MaximumIngredientCount = 50;

    public async Task<MealSessionCreationResult> CreateProductMealAsync(
        int productId, decimal grams, DataQuality quality, DateOnly mealDate, Guid? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        Product product = await FindProductAsync(productId, cancellationToken);
        MealDraft readable = ManualMealDrafts.Portion(product.Name, product.Name, grams, quality);
        return await CreateAsync(readable, MealSessionPurpose.Diary, mealDate, idempotencyKey, $"manual-meal:{productId}",
            async () => ManualMealDrafts.Portion(
                await catalog.CreateSelectionAliasAsync(productId, cancellationToken), product.Name, grams, quality),
            cancellationToken);
    }

    public async Task<MealSessionCreationResult> CreateDishAsync(
        string name, IReadOnlyList<ManualDishIngredient> ingredients, decimal finalGrams, DataQuality quality,
        DateOnly mealDate, Guid? idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ingredients);
        List<IngredientDraft> readableIngredients = [];
        foreach (ManualDishIngredient ingredient in ingredients)
        {
            Product product = await FindProductAsync(ingredient.ProductId, cancellationToken);
            readableIngredients.Add(ingredient.ToDraft(product.Name));
        }

        MealDraft readable = ManualMealDrafts.Dish(name, readableIngredients, finalGrams, quality);
        string identity = $"manual-dish:{string.Join(',', ingredients.Select(ingredient => ingredient.ProductId))}";
        return await CreateAsync(readable, MealSessionPurpose.CreateDish, mealDate, idempotencyKey, identity, async () =>
        {
            List<IngredientDraft> selected = [];
            foreach (ManualDishIngredient ingredient in ingredients)
            {
                selected.Add(ingredient.ToDraft(
                    await catalog.CreateSelectionAliasAsync(ingredient.ProductId, cancellationToken)));
            }

            return ManualMealDrafts.Dish(name, selected, finalGrams, quality);
        }, cancellationToken);
    }

    public async Task<MealSessionCreationResult> CreateSavedDishMealAsync(
        Guid dishId, decimal grams, DataQuality quality, DateOnly mealDate, Guid? idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(grams);
        StoredSavedDish dish = await dishes.FindAsync(dishId, cancellationToken)
            ?? throw new SavedDishNotFoundException();
        MealDraft readable = ManualMealDrafts.Portion(dish.Product.Name, dish.Product.Name, grams, quality);
        return await CreateAsync(readable, MealSessionPurpose.Diary, mealDate, idempotencyKey, $"saved-dish-meal:{dishId:N}",
            () => Task.FromResult(ManualMealDrafts.Portion(dish.Product.Source.Reference!, dish.Product.Name, grams, quality)),
            cancellationToken);
    }

    public async Task RequireUnambiguousSelectionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        StoredMealSession? session = await sessions.FindAsync(sessionId, cancellationToken);
        if (session is not null)
        {
            await ManualMealDrafts.RequireUnambiguousSelectionAsync(catalog, dishes, session, cancellationToken);
        }
    }

    // the messages describe the readable draft, so a repeated request is recognised by its key before a new selection
    // alias is made for nothing
    private async Task<MealSessionCreationResult> CreateAsync(
        MealDraft readable, MealSessionPurpose purpose, DateOnly mealDate, Guid? idempotencyKey,
        string requestIdentity, Func<Task<MealDraft>> select, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> messages = ManualMealDrafts.Describe(readable, purpose);
        bool repeated = idempotencyKey is { } key && key != Guid.Empty &&
                        await sessions.FindByIdempotencyKeyAsync(key, cancellationToken) is not null;
        MealDraft? draft = repeated ? null : await select();
        return await new MealWorkflowService(new FixedMealParser(draft), catalog, sessions, diary, dishes)
            .CreateAsync(messages, mealDate, cancellationToken, idempotencyKey, purpose, requestIdentity);
    }

    private async Task<Product> FindProductAsync(int productId, CancellationToken cancellationToken)
    {
        StoredProduct? product = await catalog.FindByIdAsync(productId, cancellationToken);
        return product?.Product ?? throw new ProductNotFoundException();
    }
}

public sealed record ManualDishIngredient(
    int ProductId,
    decimal WeightInGrams,
    DataQuality WeightQuality = DataQuality.Exact,
    decimal RemovedWeightInGrams = 0m,
    DataQuality RemovedWeightQuality = DataQuality.Exact)
{
    public IngredientDraft ToDraft(string productHandle) => new(
        productHandle, WeightInGrams, WeightQuality, RemovedWeightInGrams, RemovedWeightQuality);
}

public sealed class ProductNotFoundException()
    : KeyNotFoundException("Выбранный продукт недоступен или изменился. Обновите каталог и выберите его заново.");

public sealed class SavedDishNotFoundException()
    : KeyNotFoundException("Блюдо не найдено. Обновите список блюд.");
