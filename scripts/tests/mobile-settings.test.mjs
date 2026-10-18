import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const mobile = new URL("../../mobile/NutriFlow.Mobile/", import.meta.url);
const settings = readFileSync(new URL("MainPage.Settings.cs", mobile), "utf8");
const storage = readFileSync(new URL("ProfileSettings.cs", mobile), "utf8");

test("compact settings retain a screen heading and a named AI provider", () => {
    assert.match(settings, /Ui\.Title\("Настройки"\)/);
    assert.match(settings, /SemanticProperties\.SetHeadingLevel\([^,]+, SemanticHeadingLevel\.Level1\)/);
    assert.match(settings, /Ui\.Title\("Groq"\)/);
    assert.match(settings, /SemanticProperties\.SetHeadingLevel\([^,]+, SemanticHeadingLevel\.Level2\)/);
    assert.doesNotMatch(settings, /Ui\.Title\("Настройки Groq"\)/);
});

test("settings show key availability without retrieving or displaying the secret", () => {
    assert.match(settings, /bool hasKey = await ProfileSettings\.HasGroqAsync\(profileId\)/);
    assert.match(settings, /hasKey \? "Ключ сохранён" : "Ключ не добавлен"/);
    assert.match(settings, /\.AutomationId = "settings-key-status"/);
    assert.match(settings, /Ui\.Text\("Ключ Groq"\)/);
    assert.match(settings, /Entry apiKey = Ui\.Field\(hasKey \? "Новый ключ" : "Вставьте ключ"\)/);
    assert.match(settings, /Ui\.Text\("Пустое поле сохранит текущий ключ\.", true\)/);
    assert.match(settings, /apiKey\.AutomationId = "settings-api-key"/);
    assert.match(settings, /apiKey\.IsPassword = true/);
    assert.match(settings, /apiKey\.IsSpellCheckEnabled = false/);
    assert.match(settings, /apiKey\.IsTextPredictionEnabled = false/);
    assert.doesNotMatch(settings, /GetGroqAsync|SecureStorage|apiKey\.Text\s*=\s*(?:await|ProfileSettings)/);
});

test("AI data disclosure remains visible outside the collapsed model controls", () => {
    assert.match(settings, /Ui\.Text\("[^"\n]*текст[^"\n]*фото[^"\n]*аудио[^"\n]*Groq[^"\n]*"/i);
    assert.match(settings, /Ui\.Text\("[^"\n]*[Кк]люч[^"\n]*профил[^"\n]*"/);
    const modelLayout = settings.indexOf("VerticalStackLayout modelFields");
    const disclosure = settings.search(/Ui\.Text\("[^"\n]*текст[^"\n]*фото[^"\n]*аудио[^"\n]*Groq[^"\n]*"/i);
    assert.ok(disclosure >= 0 && modelLayout > disclosure,
        "Sending text, labels and audio to Groq must not be hidden with advanced model fields");
});

test("model controls are constructed once from this profile and hidden without replacing them", () => {
    assert.match(settings, /VerticalStackLayout modelFields = new\(\)\s*\{[^}]*IsVisible = false[^}]*\}/);
    assert.match(settings, /(?:modelFields\.AutomationId =|AutomationId =) "settings-model-fields"/);
    for (const [name, getter, id, placeholder] of [
        ["textModel", "TextModel", "text", "Модель текста"],
        ["visionModel", "VisionModel", "photo", "Модель фото"],
        ["speechModel", "SpeechModel", "speech", "Модель речи"]
    ]) {
        assert.equal([...settings.matchAll(new RegExp(`Entry ${name} =`, "g"))].length, 1,
            `${name} must have one persistent field per render`);
        assert.match(settings, new RegExp(`Entry ${name} = Ui\\.Field\\("${placeholder}", ProfileSettings\\.${getter}\\(profileId\\)\\)`));
        assert.match(settings, new RegExp(`${name}\\.AutomationId = "settings-model-${id}"`));
        assert.match(settings, new RegExp(`modelFields\\.Children\\.Add\\(${name}\\)`));
    }
    assert.match(settings, /foreach \(Entry model in new\[\] \{ textModel, visionModel, speechModel \}\)\s*\{\s*model\.IsSpellCheckEnabled = false;\s*model\.IsTextPredictionEnabled = false;/);
    assert.doesNotMatch(settings, /SemanticProperties\.SetDescription\((?:textModel|visionModel|speechModel|model)\s*,/);
    for (const label of ["Текст", "Фото", "Голос"]) {
        assert.match(settings, new RegExp(`modelFields\\.Children\\.Add\\(Ui\\.Text\\("${label}"\\)\\)`));
    }
});

test("model toggle only changes visibility and keeps unsaved field values intact", () => {
    const toggleStart = settings.indexOf('Ui.Action("Модели AI"');
    const toggleEnd = settings.indexOf('"settings-model-toggle"', toggleStart);
    assert.ok(toggleStart >= 0 && toggleEnd > toggleStart);
    const toggle = settings.slice(toggleStart, toggleEnd);
    assert.match(toggle, /modelFields\.IsVisible = !modelFields\.IsVisible/);
    assert.match(toggle, /return Task\.CompletedTask/);
    assert.doesNotMatch(toggle, /RenderAsync|RunAsync|ProfileSettings\.|_client\.|new Entry|\.Text\s*=|SaveGroqAsync|Launcher\./);
});

test("saving keeps the original blank-key rule and validates all three persistent model fields", () => {
    assert.match(settings, /if \(!hasKey && string\.IsNullOrWhiteSpace\(apiKey\.Text\)\)/);
    assert.match(settings, /throw new ArgumentException\("Введите личный ключ Groq\."\)/);
    assert.match(settings, /await ProfileSettings\.SaveGroqAsync\(profileId, apiKey\.Text, Ui\.Name\(textModel, "Модель текста"\), Ui\.Name\(visionModel, "Модель фото"\), Ui\.Name\(speechModel, "Модель речи"\)\)/);
    assert.match(settings, /apiKey\.Text = "";\s*await RenderAsync\(\)/);
    assert.match(settings, /\.AutomationId = "settings-save"/);
    assert.match(settings, /Ui\.Action\("Сохранить", \(\) => RunAsync\(async \(\) =>/);
});

test("key deletion remains profile-scoped and requires positive confirmation", () => {
    assert.match(settings, /if \(hasKey\)[\s\S]*Ui\.Action\("Удалить ключ", \(\) => RunAsync\(async \(\) =>[\s\S]*if \(await DisplayAlertAsync\("Удалить ключ\?",[^\n]+"Удалить", "Отмена"\)\)\s*\{\s*ProfileSettings\.RemoveGroq\(profileId\);\s*await RenderAsync\(\);/);
    assert.match(settings, /\.AutomationId = "settings-key-remove"/);
    assert.equal([...settings.matchAll(/ProfileSettings\.RemoveGroq\(/g)].length, 1);
    assert.doesNotMatch(settings, /SecureStorage\.Default\.RemoveAll|Preferences\.Default\.Clear|DeleteProfile|DeleteDatabase/);
});

test("console link and local-only data warning stay explicit without introducing auth or sync", () => {
    assert.match(settings, /Ui\.Action\("Получить ключ", \(\) => RunAsync\(\(\) => Launcher\.Default\.OpenAsync\("https:\/\/console\.groq\.com\/keys"\)\), true\)/);
    assert.match(settings, /\.AutomationId = "settings-key-console"/);
    assert.match(settings, /Ui\.Text\("На устройстве"\)|Ui\.Title\("На устройстве"\)/);
    assert.match(settings, /\.AutomationId = "settings-storage-note"/);
    assert.match(settings, /синхронизац|не синхронизир/i);
    assert.match(settings, /удалени[еяи] приложени[яе]/i);
    assert.match(settings, /Ui\.Text\("[^"\n]*дневник[^"\n]*продукты[^"\n]*блюда[^"\n]*потерян[^"\n]*"/i);
    assert.doesNotMatch(settings, /GoogleSignIn|AppleSignIn|OAuth|HttpClient|AddAuthentication/);
});

test("settings retain existing per-profile secure storage and leave model preferences untouched", () => {
    assert.match(storage, /Key\(Guid profileId, string setting\) => \$"nutriflow\.\{setting\}\.\{profileId:N\}"/);
    assert.match(storage, /if \(!string\.IsNullOrWhiteSpace\(apiKey\)\)\s*\{\s*await SecureStorage\.Default\.SetAsync\(Key\(profileId, "groq-key"\), apiKey\.Trim\(\)\);/);
    assert.match(storage, /RemoveGroq\(Guid profileId\) => SecureStorage\.Default\.Remove\(Key\(profileId, "groq-key"\)\)/);
    for (const [preference, value] of [["text-model", "textModel"], ["vision-model", "visionModel"], ["speech-model", "speechModel"]]) {
        assert.match(storage, new RegExp(`Preferences\\.Default\\.Set\\(Key\\(profileId, "${preference}"\\), ${value}\\)`));
    }
    assert.doesNotMatch(storage, /Preferences\.Default\.Set\(Key\(profileId, "groq-key"\)/);
});
