namespace NutriFlow.Domain;

public static class ProductBarcode
{
    private static readonly int[] SupportedLengths = { 8, 12, 13, 14 };

    public static string Normalize(string barcode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(barcode);

        string normalizedBarcode = barcode.Trim();

        if (!SupportedLengths.Contains(normalizedBarcode.Length) ||
            normalizedBarcode.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException(
                "A barcode must contain 8, 12, 13, or 14 digits.",
                nameof(barcode));
        }

        return normalizedBarcode;
    }
}
