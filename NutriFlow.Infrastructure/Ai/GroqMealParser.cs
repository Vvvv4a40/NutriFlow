using NutriFlow.Domain;

namespace NutriFlow.Infrastructure.Ai;

public sealed class GroqMealParser : IMealParser
{
    // groq reserves this budget against the tokens per minute of the key, a large one blocks the next request
    private const int MaximumTokens = 2000;

    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly IReadOnlyList<string> _knownProducts;

    public GroqMealParser(
        HttpClient httpClient,
        string model,
        IReadOnlyList<string>? knownProducts = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _httpClient = httpClient;
        _model = model.Trim();
        _knownProducts = knownProducts ?? [];
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

        object[] messages =
        [
            new
            {
                role = "system",
                content = AiMealDraftContract.GetInstructions(session)
            },
            new
            {
                role = "user",
                content = AiMealDraftContract.FormatInput(session, _knownProducts)
            }
        ];
        string structuredOutput = await GroqChatCompletion.CompleteAsync(
            _httpClient,
            GroqChatCompletion.CreateBody(
                _model,
                messages,
                AiMealDraftContract.SchemaName,
                AiMealDraftContract.Schema,
                MaximumTokens),
            cancellationToken);

        return AiMealDraftContract.Deserialize(structuredOutput, "Groq");
    }
}
