import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const mobile = new URL("../../mobile/NutriFlow.Mobile/", import.meta.url);
const source = name => readFileSync(new URL(name, mobile), "utf8");

test("welcome source keeps a primary start action and a separate backup recovery path", () => {
    const welcome = source("MainPage.Navigation.cs").split("private void RenderWelcome()")[1].split("private static Grid IconAction")[0];
    assert.match(welcome, /Ui\.Title\("NutriFlow"\)/);
    assert.match(welcome, /Ui\.Action\("Начать", \(\) => RunAsync\(CreateProfilePageAsync\)\)/);
    assert.match(welcome, /AutomationId = "welcome-title"/);
    assert.match(welcome, /AutomationId = "create-profile"/);
    assert.equal([...welcome.matchAll(/\bUi\.Action\(/g)].length, 2);
    assert.match(welcome, /"welcome-backup-restore"/);
    assert.doesNotMatch(welcome, /Ui\.Text\(|Ui\.Card\(|Groq|Google|Apple|LocalProfile|Профиль/);
    assert.match(welcome, /_welcome\.Content = welcome/);
    const page = source("MainPage.xaml.cs");
    assert.match(page, /Grid\.SetRowSpan\(_welcome, 3\)/);
    assert.match(page, /_top\.IsVisible = hasProfile/);
    assert.match(page, /_scroll\.IsVisible = hasProfile/);
    assert.match(page, /_welcome\.IsVisible = !hasProfile/);
    assert.match(page, /_welcome\.IsEnabled = false/);
    assert.match(page, /_welcome\.IsEnabled = true/);
});

test("short onboarding preserves local profile creation without suggesting implemented cloud login", () => {
    const form = source("MainPage.xaml.cs").split("private Task CreateProfilePageAsync()")[1].split("private Task EditEntryPageAsync")[0];
    assert.match(form, /Ui\.Title\("Твоё имя"\)/);
    assert.match(form, /Ui\.Field\("Имя"\)/);
    assert.match(form, /AutomationId = "profile-name"/);
    assert.match(form, /AutomationId = "profile-create"/);
    assert.match(form, /Ui\.Action\("Продолжить"/);
    assert.match(form, /_profiles\.CreateAsync\(Ui\.Name\(name, "Имя"\)\)/);
    assert.match(form, /await _profiles\.SelectAsync\(created\.Id\)/);
    assert.doesNotMatch(form, /Ui\.Text\(|Google|Apple|OAuth|Groq/);
});

test("rounded text fields keep the native Entry and Editor contracts", () => {
    const ui = source("Ui.cs");
    assert.match(ui, /public static Entry Field\([^\n]+=> new InputEntry\(\)/);
    assert.match(ui, /Keyboard = number \? Keyboard\.Numeric : Keyboard\.Default/);
    assert.match(ui, /ClearButtonVisibility = ClearButtonVisibility\.WhileEditing/);
    assert.match(ui, /MinimumHeightRequest = 48/);
    for (const [file, type, parent] of [["InputEntry.cs", "InputEntry", "Entry"], ["InputEditor.cs", "InputEditor", "Editor"]]) {
        const control = source(file);
        assert.match(control, new RegExp(`internal sealed class ${type} : ${parent}`));
        assert.match(control, /OnHandlerChanged\(\)[\s\S]*FieldBorder\.Apply\(this\)/);
        assert.match(control, /propertyName is nameof\(Background\) or nameof\(BackgroundColor\)/);
        assert.doesNotMatch(control, /\.Mapper\.|\.Unfocus\(|\.Focus\(/);
    }
    const styles = source("App.xaml");
    assert.match(styles, /Style TargetType="Entry" ApplyToDerivedTypes="True"/);
    assert.match(styles, /Style TargetType="Editor" ApplyToDerivedTypes="True"/);
});

test("field appearance source removes Android underline and declares focus and disabled states", () => {
    const border = source("FieldBorder.cs");
    assert.match(border, /#if ANDROID/);
    assert.match(border, /PlatformView is not Android\.Widget\.EditText/);
    assert.match(border, /nativeField\.BackgroundTintList = null/);
    assert.match(border, /nativeField\.Background = background/);
    assert.match(border, /StateListDrawable background = new/);
    assert.match(border, /-Android\.Resource\.Attribute\.StateEnabled/);
    assert.match(border, /Android\.Resource\.Attribute\.StateFocused/);
    assert.match(border, /SetCornerRadius\(14 \* density\)/);
    assert.match(border, /SetPaddingRelative\(horizontal, vertical, horizontal, vertical\)/);
    assert.doesNotMatch(border, /Handwriting|InputType|ImeOptions|Touch|field\.\w+\s*=/);
});

function luminance(hex) {
    const channels = hex.match(/[0-9a-f]{2}/gi).map(channel => parseInt(channel, 16) / 255)
        .map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
    return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
}

test("selected palette has readable text and distinguishable field boundaries", () => {
    const colors = Object.fromEntries([...source("Ui.cs").matchAll(/Color (\w+) = Color\.FromArgb\("(#[0-9a-f]{6})"\)/gi)]
        .map(match => [match[1], luminance(match[2])]));
    for (const [foreground, background, minimum] of [["TextPrimary", "Background", 4.5], ["TextPrimary", "SurfaceRaised", 4.5],
        ["Muted", "SurfaceRaised", 4.5], ["Background", "Accent", 4.5], ["FieldStroke", "SurfaceRaised", 3], ["FieldFocus", "SurfaceRaised", 3]]) {
        const contrast = (Math.max(colors[foreground], colors[background]) + 0.05) / (Math.min(colors[foreground], colors[background]) + 0.05);
        assert.ok(contrast >= minimum, `${foreground}/${background}: ${contrast.toFixed(2)} is below ${minimum}`);
    }
});

test("Ink and Azure is the selected palette", () => {
    const ui = source("Ui.cs");
    assert.match(ui, /Color Background = Color\.FromArgb\("#08151D"\)/);
    assert.match(ui, /Color Surface = Color\.FromArgb\("#112632"\)/);
    assert.match(ui, /Color Accent = Color\.FromArgb\("#62D5EF"\)/);
});

test("launcher and splash source share the chosen background and contain no old green mark", () => {
    const uiBackground = /Color Background = Color\.FromArgb\("([^"]+)"\)/.exec(source("Ui.cs"))[1];
    const project = source("NutriFlow.Mobile.csproj");
    for (const tag of ["MauiIcon", "MauiSplashScreen"]) {
        assert.match(project, new RegExp(`<${tag}[^>]+Color="${uiBackground}"`));
    }
    for (const file of ["Resources/AppIcon/appicon.svg", "Resources/Splash/splash.svg"]) {
        assert.doesNotMatch(source(file), /#185b45|#97c95c/i);
        const accent = /Color Accent = Color\.FromArgb\("([^"]+)"\)/.exec(source("Ui.cs"))[1];
        assert.ok(source(file).includes(accent));
    }
});
