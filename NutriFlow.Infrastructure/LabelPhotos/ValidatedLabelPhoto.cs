namespace NutriFlow.Infrastructure.LabelPhotos;

public sealed record ValidatedLabelPhoto(
    ReadOnlyMemory<byte> Content,
    string MediaType,
    string FileExtension);
