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

        if (kind == NutritionSourceKind.Unknown &&
            quality != DataQuality.Unknown)
        {
            throw new ArgumentException(
                "An unknown source must have unknown data quality.",
                nameof(quality));
        }

        if (kind == NutritionSourceKind.DishPhoto &&
            quality is DataQuality.Exact or DataQuality.Verified)
        {
            throw new ArgumentException(
                "Nutrition estimated from a dish photo cannot be exact or verified.",
                nameof(quality));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string normalizedName = name.Trim();
        string? normalizedReference = reference?.Trim();

        if (normalizedName.Length > 200)
        {
            throw new ArgumentException(
                "A nutrition source name cannot exceed 200 characters.",
                nameof(name));
        }

        if (reference is not null && string.IsNullOrWhiteSpace(reference))
        {
            throw new ArgumentException(
                "Reference cannot be empty or whitespace.",
                nameof(reference));
        }

        if (normalizedReference?.Length > 2048)
        {
            throw new ArgumentException(
                "A nutrition source reference cannot exceed 2048 characters.",
                nameof(reference));
        }

        if ((kind is NutritionSourceKind.LabelPhoto or
                     NutritionSourceKind.DishPhoto or
                     NutritionSourceKind.ExternalService) &&
            reference is null)
        {
            throw new ArgumentException(
                "This source requires a reference.",
                nameof(reference));
        }

        if (kind == NutritionSourceKind.WebPage && !IsHttpUrl(normalizedReference))
        {
            throw new ArgumentException(
                "A web page source requires an absolute HTTP or HTTPS URL.",
                nameof(reference));
        }

        Kind = kind;
        Quality = quality;
        Name = normalizedName;
        Reference = normalizedReference;
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
