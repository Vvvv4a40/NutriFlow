using NutriFlow.Domain;

namespace NutriFlow.Infrastructure;

public sealed record StoredProduct(
    int Id,
    Product Product);
