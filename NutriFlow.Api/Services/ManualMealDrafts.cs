using System.Globalization;
using System.Text;
using NutriFlow.Domain;
using NutriFlow.Infrastructure;

namespace NutriFlow.Api.Services;

// a calculation entered by hand is the draft the model would return, built directly. Its messages describe it, so a later
// message that parses everything again keeps what was entered
public static class ManualMealDrafts
{
    private const int MaximumMessageLength = 4000;

    public static MealDraft Portion(string productHandle, string name, decimal grams, DataQuality quality)
    {
        return new MealDraft([new DishDraft(name,
            [new IngredientDraft(productHandle, grams, quality)], grams, quality,
            [new PortionDraft(grams, quality)])], []);
    }

    public static MealDraft Dish(
        string name, IReadOnlyList<IngredientDraft> ingredients, decimal finalGrams, DataQuality quality)
    {
        return new MealDraft([new DishDraft(name, ingredients, finalGrams, quality, [])], []);
    }

    public static IReadOnlyList<string> Describe(MealDraft draft, MealSessionPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(draft);
        List<string> lines = [purpose == MealSessionPurpose.CreateDish
            ? "Ручной ввод: создать сохранённое блюдо (CreateDish)."
            : "Ручной ввод: записать съеденную порцию в дневник (Diary)."];
        foreach (DishDraft dish in draft.Dishes)
        {
            lines.Add($"Блюдо: «{dish.Name}».");
            foreach (IngredientDraft ingredient in dish.Ingredients)
            {
                lines.Add($"Ингредиент «{ingredient.ProductName}»: исходный вес {DescribeWeight(ingredient.WeightInGrams, ingredient.WeightQuality)}; " +
                    $"убрано {DescribeWeight(ingredient.RemovedWeightInGrams, ingredient.RemovedWeightQuality)}.");
            }

            lines.Add($"Готовый вес блюда: {DescribeWeight(dish.FinalWeightInGrams, dish.FinalWeightQuality)}.");
            foreach (PortionDraft portion in dish.Portions)
            {
                lines.Add(portion.WeightInGrams is not null
                    ? $"Съеденная порция: {DescribeWeight(portion.WeightInGrams, portion.WeightQuality)}."
                    : $"Съеденная доля блюда: {portion.FractionOfDish!.Value.ToString("G29", CultureInfo.InvariantCulture)} ({DescribeQuality(portion.WeightQuality)}).");
            }
        }

        List<string> messages = [];
        StringBuilder message = new();
        foreach (string line in lines)
        {
            if (message.Length + line.Length + Environment.NewLine.Length > MaximumMessageLength)
            {
                messages.Add(message.ToString().TrimEnd());
                message.Clear();
            }

            message.AppendLine(line);
        }

        messages.Add(message.ToString().TrimEnd());
        return messages;
    }

    // a session built from a hand-picked product: a new message would let the model choose by name, so the name must
    // still lead to that one product
    public static async Task RequireUnambiguousSelectionAsync(
        LocalProductCatalog catalog, SavedDishStore dishes, StoredMealSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dishes);
        ArgumentNullException.ThrowIfNull(session);
        foreach (IngredientDraft ingredient in session.Draft.Dishes.SelectMany(dish => dish.Ingredients))
        {
            string handle = ingredient.ProductName;
            bool productSelection = handle.StartsWith("selected-", StringComparison.Ordinal) &&
                                    Guid.TryParseExact(handle[9..], "N", out _);
            bool dishSelection = handle.StartsWith("saved-dish:", StringComparison.Ordinal) &&
                                 Guid.TryParseExact(handle[11..], "N", out _);
            if (!productSelection && !dishSelection)
            {
                continue;
            }

            StoredSavedDish? selectedDish = dishSelection ? await dishes.FindByNameAsync(handle, cancellationToken) : null;
            Product? selectedProduct = productSelection
                ? (await catalog.FindByNameAsync(handle, cancellationToken)).SingleOrDefault()
                : selectedDish?.Product;
            if (selectedProduct is null)
            {
                throw new ManualSelectionAmbiguousException();
            }

            IReadOnlyList<Product> products = await catalog.FindByNameAsync(selectedProduct.Name, cancellationToken);
            StoredSavedDish? namesakeDish = await dishes.FindByNameAsync(selectedProduct.Name, cancellationToken);
            if (products.Count > 1 || (products.Count != 0 && namesakeDish is not null) ||
                (dishSelection && namesakeDish?.Id != selectedDish?.Id))
            {
                throw new ManualSelectionAmbiguousException();
            }
        }
    }

    private static string DescribeWeight(decimal? grams, DataQuality quality) => grams is null
        ? "не указан"
        : $"{grams.Value.ToString("G29", CultureInfo.InvariantCulture)} г ({DescribeQuality(quality)})";

    private static string DescribeQuality(DataQuality quality) => quality == DataQuality.Estimated ? "примерно" : "точно";
}

public sealed class ManualSelectionAmbiguousException()
    : InvalidOperationException("У этого названия несколько вариантов. Создайте новый ручной черновик и выберите нужный продукт.");

// returns the draft built by hand instead of asking the model
public sealed class FixedMealParser(MealDraft? draft) : IMealParser
{
    public Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(draft ?? throw new InvalidOperationException("Для разбора текста нужен Groq-ключ."));
    }
}
