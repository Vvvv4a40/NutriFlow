import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const mobile = new URL("../../mobile/NutriFlow.Mobile/", import.meta.url);
const source = name => readFileSync(new URL(name, mobile), "utf8");

test("diary source removes redundant copy without hiding existing actions", () => {
    const diary = source("MainPage.Diary.cs");
    assert.doesNotMatch(diary, /Пока чистый лист|Добавьте первую порцию|Добавить первую порцию|Записи за день|Съедено за день|Прогресс появится/);
    assert.match(diary, /AutomationId = "diary-calories"/);
    assert.match(diary, /AutomationId = "diary-goal"/);
    assert.match(diary, /AutomationId = "diary-date"/);
    assert.match(diary, /Цель · внешняя/);
    assert.match(diary, /Съедено · внутренняя/);
    assert.match(diary, /EditGoalPageAsync\(profileId, date, progress\.Goal\)/);
    const page = source("MainPage.xaml.cs");
    assert.match(page, /Ui\.Title\("NutriFlow"\)/);
    assert.doesNotMatch(page + source("MainPage.Navigation.cs"), /_sectionLabel|Дневник питания|Питание в твоём ритме/);
    assert.match(source("MainPage.Navigation.cs"), /AutomationId = "add-food"/);
});

test("compact diary entries retain quality, revision and explicit delete confirmation", () => {
    const diary = source("MainPage.Diary.cs");
    assert.match(diary, /"Estimated" => "≈ "/);
    assert.match(diary, /_ => "\? "/);
    assert.match(diary, /SemanticProperties\.SetDescription\(action,[^\n]+Ui\.Quality\(entry\.Quality\)/);
    assert.match(diary, /DisplayActionSheetAsync\([^\n]+Ui\.Quality\(entry\.Quality\)/);
    assert.match(diary, /DisplayAlertAsync\("Удалить запись\?"/);
    assert.match(diary, /DeleteEntryAsync\(profileId, entry\.Id, entry\.Revision\)/);
    assert.match(diary, /EditEntryPageAsync\(profileId, entry\)/);
    assert.doesNotMatch(diary, /new NutritionResponse|ToNutrition\(/);
});

test("arc rendering source uses independent goal and consumed layouts, not equal progress sectors", () => {
    const arc = source("NutritionArcView.cs");
    assert.match(arc, /NutritionArcLayout\.Create\(goal\.ProteinGrams, goal\.FatGrams, goal\.CarbohydratesGrams\)/);
    assert.match(arc, /NutritionArcLayout\.Create\(consumed\.ProteinGrams, consumed\.FatGrams, consumed\.CarbohydratesGrams\)/);
    assert.doesNotMatch(arc, /Progress\(|startAngle - 60|WithAlpha\(0\.18f\)/);
    assert.match(arc, /SetIsInAccessibleTree\(this, false\)/);
    assert.match(arc, /LineCap\.Butt/);
});
