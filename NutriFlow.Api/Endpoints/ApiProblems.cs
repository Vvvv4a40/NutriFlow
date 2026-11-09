using System.Globalization;
using NutriFlow.Api.Services;
using NutriFlow.Infrastructure.Ai;

namespace NutriFlow.Api.Endpoints;

// every problem carries a machine code: the client chooses its text by the code instead of reading the detail
internal static class ApiProblems
{
    public const string CodeKey = "code";

    public static IResult Problem(int statusCode, string code, string title, string? detail = null)
    {
        return Results.Problem(
            detail: detail,
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?> { [CodeKey] = code });
    }

    public static IResult Invalid(string field, string message)
    {
        return Results.ValidationProblem(
            new Dictionary<string, string[]> { [field] = [message] },
            detail: message,
            extensions: new Dictionary<string, object?> { [CodeKey] = "validation_failed" });
    }

    public static IResult MealSessionNotFound(Guid id)
    {
        return Problem(
            StatusCodes.Status404NotFound,
            "meal_session_not_found",
            "Meal session not found.",
            $"Meal session '{id}' was not found.");
    }

    // what the ai provider did wrong, or null when the exception is not about it
    public static IResult? AiFailure(Exception exception, HttpContext context, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case GroqRateLimitException limit:
                if (limit.Wait is { } wait)
                {
                    context.Response.Headers.RetryAfter =
                        Math.Max(1, Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return Problem(
                    StatusCodes.Status503ServiceUnavailable,
                    "ai_rate_limited",
                    "The AI provider limited the requests.",
                    limit.Wait is { } seconds
                        ? $"Сервис распознавания занят. Повторите через {Math.Max(1, Math.Ceiling(seconds.TotalSeconds)).ToString(CultureInfo.InvariantCulture)} с."
                        : "Сервис распознавания занят. Повторите позже.");
            case MealParserResponseException:
                return Problem(
                    StatusCodes.Status502BadGateway,
                    "ai_invalid_response",
                    "Meal parsing failed.",
                    "The AI provider returned an invalid structured meal draft.");
            case HttpRequestException:
                return Problem(
                    StatusCodes.Status502BadGateway,
                    "ai_unavailable",
                    "Meal parsing failed.",
                    "The AI provider is temporarily unavailable.");
            case OperationCanceledException when !cancellationToken.IsCancellationRequested:
                return Problem(
                    StatusCodes.Status504GatewayTimeout,
                    "ai_timeout",
                    "Meal parsing timed out.",
                    "The AI provider did not respond before the timeout.");
            default:
                return null;
        }
    }

    // problems the endpoints do not describe themselves, such as an unreadable body or the rate limiter
    public static string DefaultCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "invalid_request",
        StatusCodes.Status404NotFound => "not_found",
        StatusCodes.Status409Conflict => "conflict",
        StatusCodes.Status412PreconditionFailed => "precondition_failed",
        StatusCodes.Status413PayloadTooLarge => "payload_too_large",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        StatusCodes.Status422UnprocessableEntity => "unprocessable",
        StatusCodes.Status428PreconditionRequired => "precondition_required",
        StatusCodes.Status429TooManyRequests => "too_many_requests",
        StatusCodes.Status502BadGateway => "external_service_failed",
        StatusCodes.Status503ServiceUnavailable => "service_unavailable",
        StatusCodes.Status504GatewayTimeout => "external_service_timeout",
        >= 500 => "internal_error",
        _ => "error"
    };

    public static IResult? ReadIdempotencyKey(HttpContext context, out Guid? idempotencyKey)
    {
        idempotencyKey = null;
        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var values))
        {
            return null;
        }

        if (values.Count != 1 ||
            !Guid.TryParseExact(values[0], "D", out Guid parsedKey) ||
            parsedKey == Guid.Empty)
        {
            return Invalid(
                "Idempotency-Key",
                "The idempotency key must be one non-empty UUID in the standard format.");
        }

        idempotencyKey = parsedKey;
        return null;
    }
}
