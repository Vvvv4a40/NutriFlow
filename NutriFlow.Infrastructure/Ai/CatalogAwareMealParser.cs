using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

// the model gets the names of known products, so the same food finds the same product; which names depends on the text
public sealed class CatalogAwareMealParser(GroqTextChain chain, LocalProductCatalog catalog) : IMealParser
{
    public async Task<MealDraft> ParseAsync(CaptureSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        string text = string.Join('\n', session.InputEvents.Select(inputEvent => inputEvent.Text));
        IReadOnlyList<string> knownProducts = await catalog.GetModelHintsAsync(text, cancellationToken);
        return await chain.CreateParser(knownProducts).ParseAsync(session, cancellationToken);
    }
}
