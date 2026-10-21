using NutriFlow.Domain;

namespace NutriFlow.Mobile.Core;

public sealed record LocalInputDraft(
    string Text,
    DateOnly MealDate,
    MealSessionPurpose Purpose,
    Guid? SessionId = null)
{
    public bool MatchesLastSubmittedMessage(IReadOnlyList<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        string text = Text?.Trim() ?? "";
        return text.Length > 0 && messages.Count > 0 &&
            string.Equals(text, messages[^1]?.Trim(), StringComparison.Ordinal);
    }
}
