namespace NutriFlow.Mobile;

internal sealed class InputEntry : Entry
{
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        FieldBorder.Apply(this);
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName is nameof(Background) or nameof(BackgroundColor))
        {
            FieldBorder.Apply(this);
        }
    }
}
