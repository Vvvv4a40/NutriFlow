using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

public sealed class OpenAiMealParser : IMealParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _model;

    public OpenAiMealParser(HttpClient httpClient, string model)
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
            store = false,
            instructions = AiMealDraftContract.GetInstructions(session),
            input = new[]
            {
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = AiMealDraftContract.FormatInput(session)
                        }
                    }
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = AiMealDraftContract.SchemaName,
                    strict = true,
                    schema = AiMealDraftContract.Schema
                }
            },
            max_output_tokens = 3000
        };

        using HttpRequestMessage request = new(HttpMethod.Post, "responses")
        {
            Content = JsonContent.Create(requestBody)
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        OpenAiResponse? responseBody;

        try
        {
            responseBody = await response.Content.ReadFromJsonAsync<OpenAiResponse>(
                SerializerOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "OpenAI returned malformed response JSON.",
                exception);
        }

        return AiMealDraftContract.Deserialize(
            ExtractStructuredOutput(responseBody),
            "OpenAI");
    }

    private static string ExtractStructuredOutput(OpenAiResponse? response)
    {
        if (response?.Output is null)
        {
            throw new InvalidDataException(
                "OpenAI response does not contain structured output.");
        }

        foreach (OpenAiOutputItem? item in response.Output)
        {
            if (item?.Content is null)
            {
                continue;
            }

            string? outputText = item.Content
                .FirstOrDefault(content =>
                    content?.Type == "output_text" &&
                    !string.IsNullOrWhiteSpace(content.Text))
                ?.Text;

            if (outputText is not null)
            {
                return outputText;
            }
        }

        throw new InvalidDataException(
            "OpenAI response does not contain structured output text.");
    }

    private sealed record OpenAiResponse(
        [property: JsonPropertyName("output")]
        IReadOnlyList<OpenAiOutputItem?>? Output);

    private sealed record OpenAiOutputItem(
        [property: JsonPropertyName("content")]
        IReadOnlyList<OpenAiContent?>? Content);

    private sealed record OpenAiContent(
        [property: JsonPropertyName("type")]
        string? Type,
        [property: JsonPropertyName("text")]
        string? Text);
}
