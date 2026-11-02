namespace NutriFlow.Ui.Components;

public sealed record SegmentedOption(string Text, string? TestId = null);

public sealed record PickerOption(string Value, string Text);
