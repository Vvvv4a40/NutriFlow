namespace NutriFlow.Infrastructure.LabelPhotos;

public static class LabelPhotoValidator
{
    public const int MaximumFileSizeInBytes = 8 * 1024 * 1024;

    public static ValidatedLabelPhoto Validate(
        ReadOnlyMemory<byte> content,
        string declaredMediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredMediaType);

        if (content.IsEmpty)
        {
            throw new InvalidDataException("The uploaded photo is empty.");
        }

        if (content.Length > MaximumFileSizeInBytes)
        {
            throw new InvalidDataException("The uploaded photo exceeds 8 MB.");
        }

        (string mediaType, string extension) = DetectFormat(content.Span);
        string normalizedDeclaredMediaType = declaredMediaType
            .Split(';', 2)[0]
            .Trim()
            .ToLowerInvariant();

        if (normalizedDeclaredMediaType != mediaType)
        {
            throw new InvalidDataException(
                "The declared media type does not match the photo content.");
        }

        return new ValidatedLabelPhoto(
            content,
            mediaType,
            extension);
    }

    private static (string MediaType, string Extension) DetectFormat(
        ReadOnlySpan<byte> content)
    {
        if (content.Length >= 8 &&
            content[..8].SequenceEqual(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return ("image/png", ".png");
        }

        if (content.Length >= 3 &&
            content[0] == 0xFF &&
            content[1] == 0xD8 &&
            content[2] == 0xFF)
        {
            return ("image/jpeg", ".jpg");
        }

        if (content.Length >= 12 &&
            content[..4].SequenceEqual("RIFF"u8) &&
            content.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ("image/webp", ".webp");
        }

        throw new InvalidDataException(
            "Only JPEG, PNG, and WebP photos are supported.");
    }
}
