import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const page = readFileSync(new URL("../../mobile/NutriFlow.Mobile/MainPage.xaml.cs", import.meta.url), "utf8");
const edit = page.slice(page.indexOf("private Task EditEntryPageAsync("), page.indexOf("private static Entry AddNumber("));

function blockAfter(source, marker) {
    const markerIndex = source.indexOf(marker);
    assert.ok(markerIndex >= 0, `Expected ${marker}`);
    const start = source.indexOf("{", markerIndex + marker.length);
    assert.ok(start >= 0, `Expected a block after ${marker}`);
    let depth = 1;
    for (let end = start + 1; end < source.length; end++) {
        if (source[end] === "{") depth++;
        if (source[end] === "}") depth--;
        if (depth === 0) return { content: source.slice(start + 1, end), start, end };
    }
    assert.fail(`Unclosed block after ${marker}`);
}

test("portion accuracy control describes the new weight without inferring mass quality from nutrition", () => {
    assert.match(edit, /Entry grams = AddNumber\(page, "Вес порции, г", entry\.WeightInGrams\)/);
    assert.match(edit, /Switch estimated = new\(\)/);
    assert.match(edit, /Ui\.Row\(estimated, Ui\.Text\("Новый вес указан примерно"\)\)/);
    assert.doesNotMatch(edit, /entry\.Quality|IsToggled\s*=\s*true/);
});

test("portion accuracy changes are tracked independently from the final switch state", () => {
    assert.match(edit, /bool qualityChanged = false;/);
    assert.match(edit, /estimated\.Toggled\s*\+=\s*\(_,\s*_\)\s*=>\s*qualityChanged = true;/);
    assert.ok(edit.indexOf("estimated.Toggled") < edit.indexOf('Ui.Action("Сохранить"'),
        "Both ON and subsequent OFF changes must be observed before saving");
    assert.doesNotMatch(edit, /qualityChanged\s*=\s*estimated\.IsToggled/);
});

test("portion weight is validated once before deciding whether an update is needed", () => {
    const save = blockAfter(edit, 'Ui.Action("Сохранить"').content;
    assert.match(save, /decimal newWeight = Ui\.Number\(grams, "Вес", true\);/);
    assert.equal([...save.matchAll(/Ui\.Number\(grams,/g)].length, 1);
    assert.ok(save.indexOf("Ui.Number(grams") < save.indexOf("if (newWeight"));
    assert.doesNotMatch(save, /Math\.Round|Math\.Abs|decimal\.Round|ToDouble|ToSingle/);
});

test("saving untouched mass and accuracy does not write a new revision or upgrade metadata", () => {
    const save = blockAfter(edit, 'Ui.Action("Сохранить"').content;
    const guard = blockAfter(save, "if (newWeight != entry.WeightInGrams || qualityChanged)");
    assert.match(guard.content, /await _client\.UpdateEntryAsync\(profileId, entry\.Id, entry\.Revision, newWeight, estimated\.IsToggled \? DataQuality\.Estimated : DataQuality\.Exact\);/);
    assert.equal([...save.matchAll(/_client\.UpdateEntryAsync\(/g)].length, 1);
    assert.doesNotMatch(save.slice(0, guard.start) + save.slice(guard.end + 1), /_client\.UpdateEntryAsync\(/);
});

test("both changed and unchanged portions close only after the guarded update completes", () => {
    const save = blockAfter(edit, 'Ui.Action("Сохранить"').content;
    const guard = blockAfter(save, "if (newWeight != entry.WeightInGrams || qualityChanged)");
    assert.doesNotMatch(guard.content, /Navigation\.PopAsync|return/);
    assert.match(save.slice(guard.end + 1), /^\s*await Navigation\.PopAsync\(\);\s*$/);
    assert.equal([...save.matchAll(/Navigation\.PopAsync\(/g)].length, 1);
});

test("portion edits retain profile identity, optimistic revision and existing refresh path", () => {
    assert.match(edit, /private Task EditEntryPageAsync\(Guid profileId, MealEntryResponse entry\)/);
    assert.match(edit, /Ui\.Action\("Сохранить", \(\) => page\.RunAsync\(async \(\) =>/);
    assert.match(edit, /UpdateEntryAsync\(profileId, entry\.Id, entry\.Revision,/);
    assert.doesNotMatch(edit, /CurrentProfileId\(|_date\s*=|new NutritionResponse|new NutritionValues|\.ScaleBy\(/);
    const appearing = blockAfter(page, "protected override async void OnAppearing()").content;
    assert.match(appearing, /await RunAsync\(async \(\) =>/);
    assert.match(appearing, /await RefreshProfilesAsync\(\);\s*await RenderAsync\(\);/);
});
