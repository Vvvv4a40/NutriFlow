using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

public sealed class GroqNutritionLabelReader : INutritionLabelReader
{
    // groq reserves this budget against the tokens per minute of the key; a photo already costs about 2000
    private const int MaximumTokens = 1200;
    private const int MaximumTokensOfSecondLook = 2500;

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
        NutritionLabelDraft? draft = null;

        try
        {
            draft = await ReadOnceAsync(imageDataUrl, null, MaximumTokens, cancellationToken);
        }
        catch (InvalidDataException)
        {
            // an impossible number, 125 g of protein in 100 g: the same misreading as numbers that disagree
        }

        if (draft is not null && !NeedsSecondLook(draft))
        {
            return draft;
        }

        // a digit was misread: only this case is worth the tokens of reasoning
        try
        {
            NutritionLabelDraft second = await ReadOnceAsync(imageDataUrl, "medium", MaximumTokensOfSecondLook, cancellationToken);
            return draft is not null && NeedsSecondLook(second) ? draft : second;
        }
        catch (Exception exception) when (draft is not null && exception is HttpRequestException or InvalidDataException)
        {
            return draft;
        }
    }

    private static bool NeedsSecondLook(NutritionLabelDraft draft) =>
        draft is { Calories: { } calories, ProteinGrams: { } protein, FatGrams: { } fat, CarbohydratesGrams: { } carbohydrates } &&
        !NutritionPlausibility.MacrosMatchCalories(calories, protein, fat, carbohydrates);

    private async Task<NutritionLabelDraft> ReadOnceAsync(
        string imageDataUrl,
        string? reasoningEffort,
        int maximumTokens,
        CancellationToken cancellationToken)
    {
        object[] messages =
        [
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
        ];
        string structuredOutput = await GroqChatCompletion.CompleteAsync(
            _httpClient,
            GroqChatCompletion.CreateBody(
                _model,
                messages,
                AiNutritionLabelContract.SchemaName,
                AiNutritionLabelContract.Schema,
                maximumTokens,
                reasoningEffort),
            cancellationToken);

        return AiNutritionLabelContract.Deserialize(structuredOutput, "Groq");
    }
}
