namespace NutriFlow.Api.Contracts;

public sealed record AddProductAliasRequest(
    string? Barcode,
    string? Alias);
