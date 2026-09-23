using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

public sealed class GroqMealParser : IMealParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _model;

    public GroqMealParser(HttpClient httpClient, string model)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _httpClient = httpClient;
        _model = model.Trim();
    }

    public async Task<MealDraft> ParseAsync(
        CaptureSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.State != CaptureSessionState.ReadyForReview)
        {
            throw new InvalidOperationException(
                "A capture session must be ready for review before parsing.");
        }

        object requestBody = new
        {
            model = _model,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = AiMealDraftContract.Instructions
                },
                new
                {
                    role = "user",
                    content = AiMealDraftContract.FormatInput(
                        session.InputEvents)
                }
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = AiMealDraftContract.SchemaName,
                    strict = true,
                    schema = AiMealDraftContract.Schema
                }
            },
            max_completion_tokens = 3000,
            stream = false,
            reasoning_effort = "none"
        };

        GroqChatResponse? responseBody = await SendAsync(
            requestBody,
            cancellationToken);
        string structuredOutput = ExtractStructuredOutput(responseBody);

        return AiMealDraftContract.Deserialize(structuredOutput, "Groq");
    }

    private async Task<GroqChatResponse?> SendAsync(
        object requestBody,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "chat/completions")
        {
            Content = JsonContent.Create(requestBody)
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        try
        {
            return await response.Content.ReadFromJsonAsync<GroqChatResponse>(
                SerializerOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Groq returned malformed response JSON.",
                exception);
        }
    }

    private static string ExtractStructuredOutput(GroqChatResponse? response)
    {
        GroqChoice? choice = response?.Choices?.FirstOrDefault();

        if (choice is null ||
            !string.Equals(
                choice.FinishReason,
                "stop",
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(choice.Message?.Content))
        {
            throw new InvalidDataException(
                "Groq response does not contain complete structured output.");
        }

        return choice.Message.Content;
    }

    private sealed record GroqChatResponse(
        [property: JsonPropertyName("choices")]
        IReadOnlyList<GroqChoice?>? Choices);

    private sealed record GroqChoice(
        [property: JsonPropertyName("message")]
        GroqMessage? Message,
        [property: JsonPropertyName("finish_reason")]
        string? FinishReason);

    private sealed record GroqMessage(
        [property: JsonPropertyName("content")]
        string? Content);
}
