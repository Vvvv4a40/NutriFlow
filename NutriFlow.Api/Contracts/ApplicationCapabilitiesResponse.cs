namespace NutriFlow.Api.Contracts;

public sealed record ApplicationCapabilitiesResponse(
    string AiProvider,
    bool SupportsFreeText,
    bool SupportsLabelPhotos);
