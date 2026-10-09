using NutriFlow.Domain;

namespace NutriFlow.Mobile.Core;

public sealed record LabelCapture(NutritionLabelDraft Draft, string PhotoReference);
