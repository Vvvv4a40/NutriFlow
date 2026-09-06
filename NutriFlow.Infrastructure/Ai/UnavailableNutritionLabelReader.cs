using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

public sealed class UnavailableNutritionLabelReader : INutritionLabelReader
{
    public Task<NutritionLabelDraft> ReadAsync(
        ReadOnlyMemory<byte> image,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "Label analysis requires the OpenAI provider to be configured.");
    }
}
