using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using NutriFlow.Api.Contracts;
using NutriFlow.Domain;

namespace NutriFlow.Api.Configuration;

internal static class OpenApiConfiguration
{
    public static void Configure(OpenApiOptions options)
    {
        options.AddSchemaTransformer((schema, context, cancellationToken) =>
        {
            Type type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
            if (type == typeof(decimal))
            {
                schema.Format = "decimal";
                if (context.ParameterDescription is null)
                {
                    schema.Type &= ~JsonSchemaType.String;
                    schema.Pattern = null;
                }
            }

            return Task.CompletedTask;
        });

        options.AddDocumentTransformer((document, context, cancellationToken) =>
        {
            SetEnumDefault(document, nameof(CreateMealSessionRequest), "purpose", nameof(MealSessionPurpose.Diary));
            SetEnumDefault(document, nameof(UpdateMealEntryRequest), "weightQuality", nameof(DataQuality.Exact));
            return Task.CompletedTask;
        });
    }

    private static void SetEnumDefault(OpenApiDocument document, string requestName, string propertyName, string value)
    {
        IOpenApiSchema property = document.Components!.Schemas![requestName].Properties![propertyName];
        if (property is OpenApiSchemaReference reference)
        {
            reference.Default = JsonValue.Create(value);
            if (reference.Target is OpenApiSchema enumSchema)
            {
                enumSchema.Default = null;
            }
        }
        else if (property is OpenApiSchema schema)
        {
            schema.Default = JsonValue.Create(value);
        }
    }
}
