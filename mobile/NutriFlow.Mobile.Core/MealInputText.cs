namespace NutriFlow.Mobile.Core;

public static class MealInputText
{
    public const int MaximumLength = 4000;

    public static string AppendTranscript(string? currentText, string? transcript)
    {
        string text = (transcript ?? "").Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("Введите или распознайте текст описания.");
        }

        string prefix = string.IsNullOrWhiteSpace(currentText) ? "" : currentText;
        int separatorLength = prefix.Length == 0 ? 0 : 1;
        long resultLength = (long)prefix.Length + separatorLength + text.Length;
        if (resultLength > MaximumLength)
        {
            long excess = resultLength - MaximumLength;
            throw new ArgumentException($"Общий лимит описания — {MaximumLength} символов. Превышение: {excess}. Сократите расшифровку или исходное описание. Тексты не изменены.");
        }

        return prefix.Length == 0 ? text : $"{prefix}\n{text}";
    }
}
