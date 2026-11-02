using NutriFlow.Mobile.Core;
using NutriFlow.Web.Services;

namespace NutriFlow.Web.Tests;

public sealed class WebProfileSettingsTests
{
    private static readonly Guid Profile = Guid.Parse("1c7dd555-ece8-43a1-a871-53faad6ebec5");
    private readonly FakeJsRuntime _js = new();

    [Fact]
    public async Task DefaultsMatchTheAndroidApp()
    {
        WebProfileSettings settings = new(_js);
        GroqSettings defaults = new("key");

        Assert.Equal("terracotta", await settings.GetThemeAsync(Profile));
        Assert.Equal(defaults.TextModel, await settings.GetTextModelAsync(Profile));
        Assert.Equal(defaults.VisionModel, await settings.GetVisionModelAsync(Profile));
        Assert.Equal(defaults.SpeechModel, await settings.GetSpeechModelAsync(Profile));
        Assert.False(await settings.HasGroqAsync(Profile));
        Assert.Null(await settings.GetSessionIdAsync(Profile));
    }

    [Fact]
    public async Task ValuesUseTheAndroidKeysPerProfile()
    {
        WebProfileSettings settings = new(_js);
        Guid session = Guid.NewGuid();

        await settings.SaveThemeAsync(Profile, "taiga");
        await settings.SaveGroqAsync(Profile, "  gsk_test  ", "text", "vision", "speech");
        await settings.SaveSessionIdAsync(Profile, session);

        Assert.Equal(new Dictionary<string, string>
        {
            ["nutriflow.theme.1c7dd555ece843a1a87153faad6ebec5"] = "taiga",
            ["nutriflow.groq-key.1c7dd555ece843a1a87153faad6ebec5"] = "gsk_test",
            ["nutriflow.text-model.1c7dd555ece843a1a87153faad6ebec5"] = "text",
            ["nutriflow.vision-model.1c7dd555ece843a1a87153faad6ebec5"] = "vision",
            ["nutriflow.speech-model.1c7dd555ece843a1a87153faad6ebec5"] = "speech",
            ["nutriflow.session.1c7dd555ece843a1a87153faad6ebec5"] = session.ToString("D")
        }, _js.LocalStorage);
        Assert.Equal(new GroqSettings("gsk_test", "text", "vision", "speech"), await settings.GetGroqAsync(Profile));
        Assert.Equal(session, await settings.GetSessionIdAsync(Profile));
        Assert.Equal("terracotta", await settings.GetThemeAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task BlankKeyKeepsTheSavedOneAndRemovalForgetsIt()
    {
        WebProfileSettings settings = new(_js);
        await settings.SaveGroqAsync(Profile, "gsk_saved", "t", "v", "s");

        await settings.SaveGroqAsync(Profile, " ", "t2", "v2", "s2");
        Assert.Equal("gsk_saved", (await settings.GetGroqAsync(Profile)).ApiKey);
        Assert.Equal("t2", (await settings.GetGroqAsync(Profile)).TextModel);

        await settings.RemoveGroqAsync(Profile);
        Assert.False(await settings.HasGroqAsync(Profile));
        InvalidOperationException missing = await Assert.ThrowsAsync<InvalidOperationException>(() => settings.GetGroqAsync(Profile));
        Assert.Equal("Добавьте личный ключ Groq в разделе «Настройки». Для ручного ввода ключ не нужен.", missing.Message);
    }

    [Fact]
    public async Task DamagedSessionIdAndClearedSessionReadAsNone()
    {
        WebProfileSettings settings = new(_js);
        _js.LocalStorage[WebProfileSettings.Key(Profile, "session")] = "not-a-guid";
        Assert.Null(await settings.GetSessionIdAsync(Profile));

        await settings.SaveSessionIdAsync(Profile, Guid.NewGuid());
        await settings.ClearSessionIdAsync(Profile);
        Assert.Null(await settings.GetSessionIdAsync(Profile));
    }

    [Fact]
    public async Task BsqliteBindingsAreKeptUnderTheAppPrefix()
    {
        BsqliteBindingStore bindings = new(_js);

        await bindings.SetProviderNameAsync("nutriflow/profile-a.db", "opfs");
        Assert.Equal("opfs", await bindings.GetProviderNameAsync("nutriflow/profile-a.db"));
        Assert.Equal(["nutriflow.bsqlite-binding.nutriflow/profile-a.db"], _js.LocalStorage.Keys);

        await bindings.RemoveAsync("nutriflow/profile-a.db");
        Assert.Null(await bindings.GetProviderNameAsync("nutriflow/profile-a.db"));
    }
}
