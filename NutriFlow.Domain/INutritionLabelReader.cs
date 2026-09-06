namespace NutriFlow.Domain;

public interface INutritionLabelReader
{
    Task<NutritionLabelDraft> ReadAsync(
        ReadOnlyMemory<byte> image,
        string mediaType,
        CancellationToken cancellationToken = default);
}
