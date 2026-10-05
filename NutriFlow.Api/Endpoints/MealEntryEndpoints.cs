using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using NutriFlow.Api.Contracts;
using NutriFlow.Infrastructure;

namespace NutriFlow.Api.Endpoints;

internal static class MealEntryEndpoints
{
    public static void MapMealEntryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/meal-entries/{id:int}", GetMealEntryAsync)
            .WithName("GetMealEntry")
            .WithSummary("Returns the current diary portion and its revision ETag.")
            .WithTags("Daily diary")
            .Produces<MealEntryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPut("/api/meal-entries/{id:int}", UpdateMealEntryAsync)
            .WithName("UpdateMealEntry")
            .WithSummary("Corrects a portion weight using the original nutrition snapshot and If-Match.")
            .WithTags("Daily diary")
            .Produces<MealEntryResponse>()
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        app.MapDelete("/api/meal-entries/{id:int}", DeleteMealEntryAsync)
            .WithName("DeleteMealEntry")
            .WithSummary("Removes a portion from the diary without deleting its original snapshot.")
            .WithTags("Daily diary")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    private static async Task<IResult> GetMealEntryAsync(
        int id,
        DailyDiaryStore diary,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        StoredMealEntry? entry = await diary.FindEntryAsync(id, cancellationToken);

        return entry is null ? EntryNotFound() : EntryResponse(entry, response);
    }

    private static async Task<IResult> UpdateMealEntryAsync(
        int id,
        UpdateMealEntryRequest? request,
        DailyDiaryStore diary,
        HttpResponse response,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        IResult? revisionError = ReadExpectedRevision(ifMatch, out int expectedRevision);

        if (revisionError is not null)
        {
            return revisionError;
        }

        if (request?.WeightInGrams is not decimal weight)
        {
            return InvalidField("weightInGrams", "The corrected portion weight is required.");
        }

        MealEntryChangeResult result;

        try
        {
            result = await diary.UpdateEntryWeightAsync(
                id, expectedRevision, weight, request.WeightQuality, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return InvalidField(
                exception.ParamName == "weightQuality" ? "weightQuality" : "weightInGrams",
                exception.Message);
        }

        return result.Kind switch
        {
            MealEntryChangeKind.Updated => EntryResponse(result.Entry!, response),
            MealEntryChangeKind.RevisionConflict => RevisionConflict(),
            _ => EntryNotFound()
        };
    }

    private static async Task<IResult> DeleteMealEntryAsync(
        int id,
        DailyDiaryStore diary,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        CancellationToken cancellationToken)
    {
        IResult? revisionError = ReadExpectedRevision(ifMatch, out int expectedRevision);

        if (revisionError is not null)
        {
            return revisionError;
        }

        MealEntryChangeResult result = await diary.DeleteEntryAsync(id, expectedRevision, cancellationToken);

        return result.Kind switch
        {
            MealEntryChangeKind.Deleted => Results.NoContent(),
            MealEntryChangeKind.RevisionConflict => RevisionConflict(),
            _ => EntryNotFound()
        };
    }

    private static IResult? ReadExpectedRevision(string? value, out int revision)
    {
        revision = 0;

        if (value is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status428PreconditionRequired,
                title: "An If-Match revision is required.",
                detail: "Send the current entry revision as a quoted ETag, for example \"0\".");
        }

        if (value is not { Length: >= 3 } || value[0] != '"' || value[^1] != '"' ||
            !int.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None,
                CultureInfo.InvariantCulture, out revision) ||
            value != FormatRevision(revision))
        {
            return InvalidField("If-Match", "Send exactly one quoted non-negative revision, for example \"0\".");
        }

        return null;
    }

    private static IResult EntryResponse(StoredMealEntry entry, HttpResponse response)
    {
        response.Headers.ETag = FormatRevision(entry.Revision);
        return Results.Ok(ResponseMapper.ToMealEntryResponse(entry));
    }

    private static string FormatRevision(int revision) => $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";

    private static IResult EntryNotFound() => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Meal entry not found.");

    private static IResult RevisionConflict() => Results.Problem(
        statusCode: StatusCodes.Status412PreconditionFailed,
        title: "The meal entry changed.",
        detail: "Load the current entry and review it before retrying the correction.");

    private static IResult InvalidField(string field, string message) => Results.ValidationProblem(
        new Dictionary<string, string[]> { [field] = [message] });
}
