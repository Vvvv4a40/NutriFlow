using System.Text.Json;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;

namespace NutriFlow.Api.Services;

internal static class MealPreviewValidator
{
    public static void Validate(JsonElement document)
    {
        foreach (JsonElement question in GetArray(document, "ClarificationQuestions"))
        {
            ValidateText(question);
        }

        foreach (JsonElement issue in GetArray(document, "Issues"))
        {
            ValidateText(GetRequiredProperty(issue, nameof(WorkflowIssueResponse.Code)));
            ValidateText(GetRequiredProperty(issue, nameof(WorkflowIssueResponse.DishName)));
            ValidateText(GetRequiredProperty(issue, nameof(WorkflowIssueResponse.Message)));
            ValidateOptionalText(issue, nameof(WorkflowIssueResponse.IngredientName));
        }

        foreach (JsonElement dish in GetArray(document, "Dishes", allowEmpty: false))
        {
            ValidateDish(dish);
        }
    }

    private static void ValidateDish(JsonElement dish)
    {
        ValidateText(GetRequiredProperty(dish, nameof(DishPreviewResponse.Name)));
        ValidateEnumName(
            GetRequiredProperty(dish, nameof(DishPreviewResponse.FinalWeightQuality)),
            typeof(DataQuality));
        ValidateOptionalNumber(dish, nameof(DishPreviewResponse.FinalWeightInGrams));
        ValidateOptionalNutrition(dish, nameof(DishPreviewResponse.TotalNutrition));
        ValidateOptionalNutrition(dish, nameof(DishPreviewResponse.NutritionPer100Grams));
        ValidateOptionalQuality(dish, nameof(DishPreviewResponse.TotalNutritionQuality));
        ValidateOptionalQuality(dish, nameof(DishPreviewResponse.NutritionPer100GramsQuality));

        foreach (JsonElement ingredient in GetArray(dish, nameof(DishPreviewResponse.Ingredients), allowEmpty: false))
        {
            ValidateIngredient(ingredient);
        }

        foreach (JsonElement portion in GetArray(dish, nameof(DishPreviewResponse.Portions)))
        {
            ValidatePortion(portion);
        }
    }

    private static void ValidateIngredient(JsonElement ingredient)
    {
        ValidateText(GetRequiredProperty(ingredient, nameof(IngredientPreviewResponse.ProductName)));
        ValidateEnumName(
            GetRequiredProperty(ingredient, nameof(IngredientPreviewResponse.WeightQuality)),
            typeof(DataQuality));
        ValidateOptionalNumber(ingredient, nameof(IngredientPreviewResponse.WeightInGrams));
        ValidateOptionalNumber(ingredient, nameof(IngredientPreviewResponse.RemovedWeightInGrams), allowZero: true);
        ValidateOptionalNumber(ingredient, nameof(IngredientPreviewResponse.IncludedWeightInGrams), allowZero: true);
        ValidateOptionalQuality(ingredient, nameof(IngredientPreviewResponse.RemovedWeightQuality));
        ValidateOptionalNutrition(ingredient, nameof(IngredientPreviewResponse.Nutrition));

        JsonElement product = GetOptionalProperty(ingredient, nameof(IngredientPreviewResponse.ResolvedProduct));
        if (HasValue(product))
        {
            ValidateText(GetRequiredProperty(product, nameof(ProductResponse.Name)));
            ValidateText(GetRequiredProperty(product, nameof(ProductResponse.SourceName)));
            ValidateEnumName(GetRequiredProperty(product, nameof(ProductResponse.SourceKind)), typeof(NutritionSourceKind));
            ValidateEnumName(GetRequiredProperty(product, nameof(ProductResponse.DataQuality)), typeof(DataQuality));
            ValidateOptionalText(product, nameof(ProductResponse.SourceReference));
            ValidateOptionalText(product, nameof(ProductResponse.Barcode));
            ValidateNutrition(product);
        }
    }

    private static void ValidatePortion(JsonElement portion)
    {
        ValidateEnumName(
            GetRequiredProperty(portion, nameof(PortionPreviewResponse.WeightQuality)),
            typeof(DataQuality));
        JsonElement fraction = GetOptionalProperty(portion, nameof(PortionPreviewResponse.FractionOfDish));
        ValidateOptionalNumber(portion, nameof(PortionPreviewResponse.WeightInGrams), allowZero: HasValue(fraction));
        ValidateOptionalNumber(portion, nameof(PortionPreviewResponse.FractionOfDish), maximum: 1m);
        ValidateOptionalNutrition(portion, nameof(PortionPreviewResponse.Nutrition));
        ValidateOptionalQuality(portion, nameof(PortionPreviewResponse.NutritionQuality));

        if (!HasValue(GetOptionalProperty(portion, nameof(PortionPreviewResponse.WeightInGrams))) &&
            !HasValue(fraction))
        {
            throw new InvalidDataException("Stored meal preview portion has neither a weight nor a fraction.");
        }
    }

    private static void ValidateOptionalNutrition(JsonElement parent, string propertyName)
    {
        JsonElement nutrition = GetOptionalProperty(parent, propertyName);
        if (HasValue(nutrition))
        {
            ValidateNutrition(nutrition);
        }
    }

    private static void ValidateNutrition(JsonElement nutrition)
    {
        ValidateNumber(GetRequiredProperty(nutrition, nameof(NutritionResponse.Calories)), allowZero: true);
        ValidateNumber(GetRequiredProperty(nutrition, nameof(NutritionResponse.ProteinGrams)), allowZero: true);
        ValidateNumber(GetRequiredProperty(nutrition, nameof(NutritionResponse.FatGrams)), allowZero: true);
        ValidateNumber(GetRequiredProperty(nutrition, nameof(NutritionResponse.CarbohydratesGrams)), allowZero: true);
    }

    private static void ValidateOptionalQuality(JsonElement parent, string propertyName)
    {
        JsonElement quality = GetOptionalProperty(parent, propertyName);
        if (HasValue(quality))
        {
            ValidateEnumName(quality, typeof(DataQuality));
        }
    }

    private static void ValidateEnumName(JsonElement value, Type enumType)
    {
        ValidateText(value);
        if (!Enum.IsDefined(enumType, value.GetString()!))
        {
            throw new InvalidDataException("Stored meal preview contains an unknown enum name.");
        }
    }

    private static void ValidateOptionalText(JsonElement parent, string propertyName)
    {
        JsonElement value = GetOptionalProperty(parent, propertyName);
        if (HasValue(value))
        {
            ValidateText(value);
        }
    }

    private static void ValidateText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException("Stored meal preview contains blank or non-string text.");
        }
    }

    private static void ValidateOptionalNumber(
        JsonElement parent,
        string propertyName,
        bool allowZero = false,
        decimal? maximum = null)
    {
        JsonElement value = GetOptionalProperty(parent, propertyName);
        if (HasValue(value))
        {
            ValidateNumber(value, allowZero, maximum);
        }
    }

    private static void ValidateNumber(JsonElement value, bool allowZero, decimal? maximum = null)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out decimal number) ||
            (allowZero ? number < 0m : number <= 0m) || (maximum is not null && number > maximum.Value))
        {
            throw new InvalidDataException("Stored meal preview contains an invalid numeric value.");
        }
    }

    private static JsonElement.ArrayEnumerator GetArray(JsonElement parent, string propertyName, bool allowEmpty = true)
    {
        JsonElement value = GetRequiredProperty(parent, propertyName);
        if (value.ValueKind != JsonValueKind.Array || (!allowEmpty && value.GetArrayLength() == 0))
        {
            throw new InvalidDataException($"Stored meal preview array '{propertyName}' is invalid.");
        }

        return value.EnumerateArray();
    }

    private static JsonElement GetRequiredProperty(JsonElement parent, string propertyName)
    {
        JsonElement value = GetOptionalProperty(parent, propertyName);
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"Stored meal preview field '{propertyName}' is missing.");
        }

        return value;
    }

    private static JsonElement GetOptionalProperty(JsonElement parent, string propertyName)
    {
        JsonElement value = default;
        if (parent.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in parent.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                }
            }
        }

        return value;
    }

    private static bool HasValue(JsonElement value)
    {
        return value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }
}
