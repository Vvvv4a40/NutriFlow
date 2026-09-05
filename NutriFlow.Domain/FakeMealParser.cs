namespace NutriFlow.Domain;

public sealed class FakeMealParser
{
    private static readonly string[] SupportedInputTexts =
    {
        "Добавил 200 г демо-продукта A.",
        "Потом добавил 100 г демо-продукта B.",
        "Готовое блюдо весит 250 г.",
        "Съел 125 г, потом ещё две порции по 62,5 г."
    };

    private static readonly string[] AmbiguousInputTexts =
    {
        "Добавил 500 г макарон.",
        "Готовое блюдо весит 900 г.",
        "Съел 300 г."
    };

    private static readonly string[] ResolvedAmbiguousInputTexts =
    {
        "Добавил 500 г макарон.",
        "Готовое блюдо весит 900 г.",
        "Съел 300 г.",
        "В сухом виде."
    };

    public MealDraft Parse(CaptureSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.State != CaptureSessionState.ReadyForReview)
        {
            throw new InvalidOperationException(
                "A capture session must be ready for review before parsing.");
        }

        if (MatchesInput(session.InputEvents, SupportedInputTexts))
        {
            return CreateSupportedDraft();
        }

        if (MatchesInput(session.InputEvents, AmbiguousInputTexts))
        {
            return CreateAmbiguousDraft();
        }

        if (MatchesInput(session.InputEvents, ResolvedAmbiguousInputTexts))
        {
            return CreateResolvedAmbiguousDraft();
        }

        throw new NotSupportedException(
            "FakeMealParser supports only its predefined input sequences.");
    }

    private static MealDraft CreateSupportedDraft()
    {
        DishDraft dish = new DishDraft(
            name: "Демо-блюдо",
            ingredients: new List<IngredientDraft>
            {
                new IngredientDraft("Демо-продукт A", 200m),
                new IngredientDraft("Демо-продукт B", 100m)
            },
            finalWeightInGrams: 250m,
            portionWeightsInGrams: new List<decimal>
            {
                125m,
                62.5m,
                62.5m
            });

        return new MealDraft(
            dishes: new List<DishDraft> { dish },
            clarificationQuestions: new List<string>());
    }

    private static MealDraft CreateAmbiguousDraft()
    {
        DishDraft dish = new DishDraft(
            name: "Макароны",
            ingredients: new List<IngredientDraft>
            {
                new IngredientDraft("Макароны", 500m)
            },
            finalWeightInGrams: 900m,
            portionWeightsInGrams: new List<decimal> { 300m });

        return new MealDraft(
            dishes: new List<DishDraft> { dish },
            clarificationQuestions: new List<string>
            {
                "500 г макарон указаны в сухом или уже сваренном виде?"
            });
    }

    private static MealDraft CreateResolvedAmbiguousDraft()
    {
        DishDraft dish = new DishDraft(
            name: "Макароны",
            ingredients: new List<IngredientDraft>
            {
                new IngredientDraft("Макароны сухие", 500m)
            },
            finalWeightInGrams: 900m,
            portionWeightsInGrams: new List<decimal> { 300m });

        return new MealDraft(
            dishes: new List<DishDraft> { dish },
            clarificationQuestions: new List<string>());
    }

    private static bool MatchesInput(
        IReadOnlyList<InputEvent> inputEvents,
        IReadOnlyList<string> expectedTexts)
    {
        if (inputEvents.Count != expectedTexts.Count)
        {
            return false;
        }

        for (int index = 0; index < expectedTexts.Count; index++)
        {
            if (inputEvents[index].Text != expectedTexts[index])
            {
                return false;
            }
        }

        return true;
    }
}
