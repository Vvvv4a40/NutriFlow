namespace NutriFlow.Domain;

public enum NutritionSourceKind
{
    Unknown = 0,
    NutriFlowCatalog = 1,
    ManualInput = 2,
    LabelPhoto = 3,
    ExternalService = 4,
    WebPage = 5,
    DishPhoto = 6
}
