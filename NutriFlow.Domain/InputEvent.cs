namespace NutriFlow.Domain;

public sealed class InputEvent
{
    public InputEvent(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        Text = text;
    }

    public string Text { get; }
}
