using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

// the main model does not wait for its limit: the reserve answers at once, and only that one is worth waiting for.
// So the main client should give up on a 429 right away and the reserve client may wait
public sealed class GroqTextChain(HttpClient main, string mainModel, HttpClient reserve, string reserveModel)
{
    public INutritionEstimator CreateEstimator() => new ReserveNutritionEstimator(
        new GroqNutritionEstimator(main, mainModel), new GroqNutritionEstimator(reserve, reserveModel));

    public IMealParser CreateParser(IReadOnlyList<string> knownProducts) => new ReserveMealParser(
        new GroqMealParser(main, mainModel, knownProducts), new GroqMealParser(reserve, reserveModel, knownProducts));
}
