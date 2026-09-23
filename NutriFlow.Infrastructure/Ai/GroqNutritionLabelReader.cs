using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

public sealed class GroqNutritionLabelReader : INutritionLabelReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _model;

    public GroqNutritionLabelReader(HttpClient httpClient, string model)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _httpClient = httpClient;
        _model = model.Trim();
    }

    public async Task<NutritionLabelDraft> ReadAsync(
        ReadOnlyMemory<byte> image,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        if (image.IsEmpty)
        {
            throw new ArgumentException("A label image is required.", nameof(image));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        string imageDataUrl =
            $"data:{mediaType};base64,{Convert.ToBase64String(image.Span)}";
        object requestBody = new
        {
            model = _model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = AiNutritionLabelContract.Instructions
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = AiNutritionLabelContract.InputPrompt
                        },
                        new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = imageDataUrl
                            }
                        }
                    }
                }
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = AiNutritionLabelContract.SchemaName,
                    strict = true,
                    schema = AiNutritionLabelContract.Schema
                }
            },
            max_completion_tokens = 1500,
            stream = false,
            reasoning_effort = "none"
        };

        GroqChatResponse? responseBody = await SendAsync(
            requestBody,
            cancellationToken);
        string structuredOutput = ExtractStructuredOutput(responseBody);

        return AiNutritionLabelContract.Deserialize(structuredOutput, "Groq");
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
                "Groq response does not contain complete structured label output.");
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
