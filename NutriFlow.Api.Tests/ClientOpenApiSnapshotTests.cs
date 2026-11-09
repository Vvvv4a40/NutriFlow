using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NutriFlow.Api.Tests;

// the client builds its typescript types from client/openapi.json; an api change without a new snapshot would leave the
// client describing endpoints that no longer exist
public sealed class ClientOpenApiSnapshotTests
{
    private const string UpdateVariable = "NUTRIFLOW_UPDATE_OPENAPI";

    [Fact]
    public async Task ClientSnapshot_MatchesTheDocumentTheServerPublishes()
    {
        await using TestApiFactory factory = new(applyMigrations: false, seedDemoData: false);
        using HttpClient client = factory.CreateClient();
        string current = Normalize(await client.GetStringAsync("/openapi/v1.json"));
        string path = Path.Combine(FindRepositoryRoot(), "client", "openapi.json");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, current);
        }

        Assert.True(File.Exists(path), $"client/openapi.json is missing. Run the tests with {UpdateVariable}=1 to create it.");
        string[] snapshot = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n").Split('\n');
        string[] published = current.Split('\n');
        int line = Enumerable.Range(0, Math.Max(snapshot.Length, published.Length))
            .FirstOrDefault(index => index >= snapshot.Length || index >= published.Length ||
                                     snapshot[index] != published[index], -1);

        if (line >= 0)
        {
            Assert.Fail(
                $"client/openapi.json differs from the published document at line {line + 1}: " +
                $"snapshot '{(line < snapshot.Length ? snapshot[line].Trim() : "<end>")}', " +
                $"server '{(line < published.Length ? published[line].Trim() : "<end>")}'. " +
                $"If the change is intended, run the tests with {UpdateVariable}=1 and review the diff of the snapshot.");
        }
    }

    private static string Normalize(string json)
    {
        JsonObject document = JsonNode.Parse(json)!.AsObject();
        // the address of the test server is not part of the contract
        document.Remove("servers");
        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        return document.ToJsonString(options).ReplaceLineEndings("\n") + "\n";
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NutriFlow.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository root with NutriFlow.sln was not found.");
    }
}
