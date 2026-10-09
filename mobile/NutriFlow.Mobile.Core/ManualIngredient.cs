using NutriFlow.Api.Contracts;
using NutriFlow.Domain;

namespace NutriFlow.Mobile.Core;

public sealed record ManualIngredient(
    ProductResponse Product,
    decimal WeightInGrams,
    DataQuality WeightQuality = DataQuality.Exact,
    decimal RemovedWeightInGrams = 0m,
    DataQuality RemovedWeightQuality = DataQuality.Exact);
