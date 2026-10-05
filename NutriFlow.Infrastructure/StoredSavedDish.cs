using NutriFlow.Domain;

namespace NutriFlow.Infrastructure;

public sealed record StoredSavedDish(
    Guid Id,
    Guid SourceSessionId,
    Product Product,
    decimal FinalWeightInGrams,
    DateTimeOffset CreatedAtUtc);
