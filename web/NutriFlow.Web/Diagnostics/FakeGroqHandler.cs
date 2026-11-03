using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NutriFlow.Web.Diagnostics;

// development only: with the Groq key «fake» the text parser answers here, without network or a real key.
// It reads «name 150 г» items separated by commas or «и» and asks for a missing weight, which is enough to walk
// through parsing, clarifications and the preview; every other request goes to the real Groq
public sealed partial class FakeGroqHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public const string Key = "fake";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.Parameter != Key ||
            request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) != true ||
            request.Content is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        JsonNode body = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!;
        JsonArray messages = body["messages"]!.AsArray();
        bool dish = messages[0]!["content"]!.GetValue<string>().Contains("The session purpose is CreateDish", StringComparison.Ordinal);
        string draft = Parse(messages[1]!["content"]!.GetValue<string>(), dish).ToJsonString();
        string completion = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["content"] = draft },
                ["finish_reason"] = "stop"
            })
        }.ToJsonString();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(completion, Encoding.UTF8, "application/json")
        };
    }

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
