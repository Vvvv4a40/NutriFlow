using NutriFlow.Api.Contracts;
using NutriFlow.Domain;
using NutriFlow.Infrastructure.Audio;
using NutriFlow.Infrastructure.LabelPhotos;

namespace NutriFlow.Api.Endpoints;

internal static class MediaEndpoints
{
    public static void MapMediaEndpoints(this WebApplication app)
    {
        app.MapPost("/api/audio/transcribe", TranscribeSpeechAsync)
            .WithName("TranscribeSpeech")
            .RequireRateLimiting("external-services")
            .WithSummary("Transcribes an audio upload into text for user review.")
            .WithTags("Audio")
            .DisableAntiforgery()
            .Produces<SpeechTranscriptionResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        app.MapPost("/api/labels/analyze", AnalyzeNutritionLabelAsync)
            .WithName("AnalyzeNutritionLabel")
            .RequireRateLimiting("external-services")
            .WithSummary("Extracts a reviewable nutrition draft from a label photo.")
            .WithTags("Labels")
            .DisableAntiforgery()
            .Produces<NutritionLabelDraftResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        app.MapGet("/api/label-photos/{fileName}", GetLabelPhotoAsync)
            .WithName("GetLabelPhoto")
            .WithSummary("Returns a saved nutrition-label photo by its opaque reference.")
            .WithTags("Labels")
            .Produces(
                StatusCodes.Status200OK,
                contentType: "image/jpeg",
                additionalContentTypes: ["image/png", "image/webp"])
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> TranscribeSpeechAsync(
        IFormFile? audio,
        ISpeechTranscriber transcriber,
        CancellationToken cancellationToken)
    {
        if (audio is null || audio.Length == 0 ||
            audio.Length > AudioUploadValidator.MaximumFileSizeInBytes)
        {
            return InvalidAudio("An audio recording between 1 byte and 8 MB is required.");
        }

        ValidatedAudio validatedAudio;

        try
        {
            await using MemoryStream buffer = new((int)audio.Length);
            await audio.CopyToAsync(buffer, cancellationToken);
            validatedAudio = AudioUploadValidator.Validate(
                buffer.ToArray(),
                audio.ContentType);
        }
        catch (InvalidDataException exception)
        {
            return InvalidAudio(exception.Message);
        }

        try
        {
            string text = await transcriber.TranscribeAsync(
                validatedAudio,
                cancellationToken);

            return string.IsNullOrWhiteSpace(text)
                ? Results.Problem(
                    detail: "No speech was recognized. Try a clearer recording.",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "No speech recognized.")
                : Results.Ok(new SpeechTranscriptionResponse(text));
        }
        catch (NotSupportedException exception)
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Speech transcription is not configured.");
        }
        catch (InvalidDataException)
        {
            return Results.Problem(
                detail: "The AI provider returned invalid transcription data.",
                statusCode: StatusCodes.Status502BadGateway,
                title: "Speech transcription failed.");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                detail: "The AI provider is temporarily unavailable.",
                statusCode: StatusCodes.Status502BadGateway,
                title: "Speech transcription failed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                detail: "The AI provider did not respond before the timeout.",
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "Speech transcription timed out.");
        }
    }

    private static async Task<IResult> AnalyzeNutritionLabelAsync(
        IFormFile photo,
        INutritionLabelReader labelReader,
        LabelPhotoStore photoStore,
        CancellationToken cancellationToken)
    {
        if (photo.Length == 0)
        {
            return InvalidPhoto("A label photo is required.");
        }

        if (photo.Length > LabelPhotoValidator.MaximumFileSizeInBytes)
        {
            return InvalidPhoto("The label photo cannot exceed 8 MB.");
        }

        byte[] content;

        await using (MemoryStream buffer = new MemoryStream((int)photo.Length))
        {
            await photo.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
        }

        ValidatedLabelPhoto validatedPhoto;

        try
        {
            validatedPhoto = LabelPhotoValidator.Validate(content, photo.ContentType);
        }
        catch (InvalidDataException exception)
        {
            return InvalidPhoto(exception.Message);
        }

        try
        {
            NutritionLabelDraft draft = await labelReader.ReadAsync(
                validatedPhoto.Content,
                validatedPhoto.MediaType,
                cancellationToken);
            string photoReference = await photoStore.SaveAsync(
                validatedPhoto,
                cancellationToken);

            return Results.Ok(new NutritionLabelDraftResponse(
                photoReference,
                draft.ProductName,
                draft.Basis.ToString(),
                draft.Calories,
                draft.ProteinGrams,
                draft.FatGrams,
                draft.CarbohydratesGrams,
                new List<string>(draft.ClarificationQuestions),
                draft.CanCreateProduct));
        }
        catch (NotSupportedException exception)
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Label analysis is not configured.");
        }
        catch (InvalidDataException)
        {
            return Results.Problem(
                detail: "The AI provider returned invalid label data.",
                statusCode: StatusCodes.Status502BadGateway,
                title: "Label analysis failed.");
        }
        catch (HttpRequestException)
        {
            return Results.Problem(
                detail: "The AI provider is temporarily unavailable.",
                statusCode: StatusCodes.Status502BadGateway,
                title: "Label analysis failed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                detail: "The AI provider did not respond before the timeout.",
                statusCode: StatusCodes.Status504GatewayTimeout,
                title: "Label analysis timed out.");
        }
    }

    private static async Task<IResult> GetLabelPhotoAsync(
        string fileName,
        LabelPhotoStore photoStore,
        CancellationToken cancellationToken)
    {
        StoredLabelPhoto? photo = await photoStore.FindAsync(
            $"label-photo:{fileName}", cancellationToken);

        return photo is null
            ? Results.Problem(
                detail: "The saved label photo was not found.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Label photo not found.")
            : Results.File(
                photo.FilePath,
                photo.MediaType,
                enableRangeProcessing: true);
    }

    private static IResult InvalidAudio(string message)
    {
        return Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["audio"] = new[] { message }
            });
    }

    private static IResult InvalidPhoto(string message)
    {
        return Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["photo"] = new[] { message }
            });
    }
}
