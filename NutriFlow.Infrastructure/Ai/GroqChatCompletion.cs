using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NutriFlow.Infrastructure.Ai;

// one structured chat request to groq; the models differ in which reasoning settings they accept
internal static class GroqChatCompletion
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static Dictionary<string, object?> CreateBody(
        string model,
        object[] messages,
        string schemaName,
        JsonElement schema,
        int maximumTokens,
        string? reasoningEffort = null)
    {
        Dictionary<string, object?> body = new()
        {
            ["model"] = model,
            ["messages"] = messages,
            ["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = schemaName,
                    strict = true,
                    schema
                }
            },
            ["max_completion_tokens"] = maximumTokens,
            ["stream"] = false
        };

        // gpt-oss refuses "none" and reasons a little at the lowest setting; a model unknown here keeps its own default
        if (model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase))
        {
            body["reasoning_effort"] = reasoningEffort ?? "low";
            body["include_reasoning"] = false;
        }
        else if (model.Contains("qwen", StringComparison.OrdinalIgnoreCase))
        {
            body["reasoning_effort"] = reasoningEffort ?? "none";
            if (reasoningEffort is not null)
            {
                body["reasoning_format"] = "hidden";
            }
        }

        return body;
    }

    internal static async Task<string> CompleteAsync(
        HttpClient httpClient,
        object body,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(body)
        };
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        GroqChatResponse? parsed;

        try
        {
            parsed = await response.Content.ReadFromJsonAsync<GroqChatResponse>(
                SerializerOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Groq returned malformed response JSON.",
                exception);
        }

        GroqChoice? choice = parsed?.Choices?.FirstOrDefault();

        if (choice is null ||
            !string.Equals(choice.FinishReason, "stop", StringComparison.Ordinal) ||
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
