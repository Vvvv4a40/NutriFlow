namespace NutriFlow.Domain;

public sealed class NutritionSource
{
    public NutritionSource(
        NutritionSourceKind kind,
        DataQuality quality,
        string name,
        string? reference = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(quality))
        {
            throw new ArgumentOutOfRangeException(nameof(quality));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (reference is not null && string.IsNullOrWhiteSpace(reference))
        {
            throw new ArgumentException(
                "Reference cannot be empty or whitespace.",
                nameof(reference));
        }

        if ((kind is NutritionSourceKind.LabelPhoto or NutritionSourceKind.DishPhoto) &&
            reference is null)
        {
            throw new ArgumentException(
                "A photo source requires a reference.",
                nameof(reference));
        }

        if (kind == NutritionSourceKind.WebPage && !IsHttpUrl(reference))
        {
            throw new ArgumentException(
                "A web page source requires an absolute HTTP or HTTPS URL.",
                nameof(reference));
        }

        Kind = kind;
        Quality = quality;
        Name = name.Trim();
        Reference = reference?.Trim();
    }

    public NutritionSourceKind Kind { get; }
    public DataQuality Quality { get; }
    public string Name { get; }
    public string? Reference { get; }

    private static bool IsHttpUrl(string? reference)
    {
        return Uri.TryCreate(reference, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp ||
                uri.Scheme == Uri.UriSchemeHttps);
    }
}
