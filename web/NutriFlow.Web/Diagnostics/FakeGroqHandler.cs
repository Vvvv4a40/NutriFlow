using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NutriFlow.Web.Diagnostics;

// development only: with the Groq key «fake» the requests of the app are answered here, without network or a real key.
// Text: «name 150 г» items separated by commas or «и», with a question about a missing weight, which is enough to walk
// through parsing, clarifications and the preview. Speech: a fixed transcript in that format. A label photo: fixed values
// per 100 g, whatever is on the photo. With «fake-serving» the label has values per a serving and a question, with
// «fake-down» every request fails as if Groq were down. Every other key goes to the real Groq
public sealed partial class FakeGroqHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public const string Key = "fake";
    public const string ServingKey = "fake-serving";
    public const string DownKey = "fake-down";
    public const string Transcript = "гречка 150 г, куриная грудка 120 г";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? key = request.Headers.Authorization?.Parameter;
        if (key is not (Key or ServingKey or DownKey) || request.RequestUri is null || request.Content is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        if (key == DownKey)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        string path = request.RequestUri.AbsolutePath;
        if (path.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
        {
            return Json(new JsonObject { ["text"] = Transcript });
        }

        if (!path.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        JsonNode body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!;
        JsonArray messages = body["messages"]!.AsArray();
        // only the request with a photo sends its user part as a list of text and image
        JsonObject answer = messages[1]!["content"] is JsonArray
            ? Label(key == ServingKey)
            : Parse(messages[1]!["content"]!.GetValue<string>(),
                messages[0]!["content"]!.GetValue<string>().Contains("The session purpose is CreateDish", StringComparison.Ordinal));
        return Json(new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["content"] = answer.ToJsonString() },
                ["finish_reason"] = "stop"
            })
        });
    }

    private static HttpResponseMessage Json(JsonObject content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content.ToJsonString(), Encoding.UTF8, "application/json")
    };

    internal static JsonObject Label(bool perServing) => perServing
        ? new JsonObject
        {
            ["productName"] = "Батончик с этикетки",
            ["basis"] = "per_serving",
            ["calories"] = 180m,
            ["proteinGrams"] = 5m,
            ["fatGrams"] = 7.5m,
            ["carbohydratesGrams"] = null,
            ["clarificationQuestions"] = new JsonArray("Сколько граммов в порции?", "Сколько углеводов в порции?")
        }
        : new JsonObject
        {
            ["productName"] = "Йогурт с этикетки",
            ["basis"] = "per_100g",
            ["calories"] = 62m,
            ["proteinGrams"] = 4.1m,
            ["fatGrams"] = 1.5m,
            ["carbohydratesGrams"] = 6.8m,
            ["clarificationQuestions"] = new JsonArray()
        };

    // the user part of the request lists the messages as «1. text»; a later message overrides an earlier weight
    internal static JsonObject Parse(string input, bool dish)
    {
        Dictionary<string, (string Name, decimal? Grams)> items = new(StringComparer.CurrentCultureIgnoreCase);
        decimal? finalWeight = null;
        foreach (Match line in NumberedLine().Matches(input))
        {
            foreach (string part in Separator().Split(line.Groups["text"].Value))
            {
                string text = part.Trim().TrimEnd('.');
                if (text.Length == 0)
                {
                    continue;
                }

                Match weighed = Weighed().Match(text);
                string name = weighed.Success ? weighed.Groups["name"].Value.Trim() : text;
                decimal? grams = weighed.Success
                    ? decimal.Parse(weighed.Groups["grams"].Value.Replace(',', '.'), CultureInfo.InvariantCulture)
                    : null;
                // a bare «150 г» answers the first question about a weight
                if (name.Length == 0)
                {
                    name = items.Values.FirstOrDefault(item => item.Grams is null).Name ?? "";
                    if (name.Length == 0)
                    {
                        continue;
                    }
                }

                if (dish && name.StartsWith("готов", StringComparison.CurrentCultureIgnoreCase))
                {
                    finalWeight = grams ?? finalWeight;
                }
                else if (grams is not null || !items.ContainsKey(name))
                {
                    items[name] = (name, grams);
                }
            }
        }

        JsonArray questions = [];
        foreach ((string name, decimal? grams) in items.Values.Where(item => item.Grams is null))
        {
            questions.Add($"Сколько граммов «{name}»?");
        }

        JsonArray dishes = [];
        if (dish)
        {
            if (finalWeight is null)
            {
                questions.Add("Сколько весит готовое блюдо?");
            }

            dishes.Add(Dish("Моё блюдо", items.Values.Select(item => Ingredient(item.Name, item.Grams)), finalWeight, []));
        }
        else
        {
            foreach ((string name, decimal? grams) in items.Values)
            {
                JsonObject[] portions = grams is null ? [] : [Portion(grams.Value)];
                dishes.Add(Dish(name, [Ingredient(name, grams)], grams, portions));
            }
        }

        return new JsonObject { ["dishes"] = dishes, ["clarificationQuestions"] = questions };
    }

    private static JsonObject Dish(string name, IEnumerable<JsonObject> ingredients, decimal? finalWeight, JsonObject[] portions) => new()
    {
        ["name"] = name,
        ["ingredients"] = new JsonArray([.. ingredients]),
        ["finalWeightInGrams"] = finalWeight,
        ["finalWeightQuality"] = finalWeight is null ? "unknown" : "exact",
        ["portions"] = new JsonArray([.. portions])
    };

    private static JsonObject Ingredient(string name, decimal? grams) => new()
    {
        ["productName"] = name,
        ["weightInGrams"] = grams,
        ["weightQuality"] = grams is null ? "unknown" : "exact",
        ["removedWeightInGrams"] = 0m,
        ["removedWeightQuality"] = "exact"
    };

    private static JsonObject Portion(decimal grams) => new()
    {
        ["weightInGrams"] = grams,
        ["fractionOfDish"] = null,
        ["weightQuality"] = "exact"
    };

    [GeneratedRegex(@"^\d+\. (?<text>.+)$", RegexOptions.Multiline)]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@",(?!\d)|;|\.\s+|\s+и\s+")]
    private static partial Regex Separator();

    [GeneratedRegex(@"^(?<name>.*?)\s*(?<grams>\d+(?:[.,]\d+)?)\s*(?:г|гр|грамм\w*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Weighed();
}
