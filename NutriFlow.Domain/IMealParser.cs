namespace NutriFlow.Domain;

public interface IMealParser
{
    Task<MealDraft> ParseAsync(
        CaptureSession session,
        CancellationToken cancellationToken = default);
}
