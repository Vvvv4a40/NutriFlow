import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const mobile = new URL("../../mobile/NutriFlow.Mobile/", import.meta.url);
const source = name => readFileSync(new URL(name, mobile), "utf8");

test("macro summaries use a whole-number display format without changing weight precision", () => {
    const ui = source("Ui.cs");
    assert.match(ui, /string MacroAmount\(decimal value\) => value\.ToString\("0", CultureInfo\.CurrentCulture\)/);
    assert.match(ui, /string Amount\(decimal value\) => value\.ToString\("0\.##", CultureInfo\.CurrentCulture\)/);
    for (const macro of ["Protein", "Fat", "Carbohydrates"]) {
        assert.equal([...ui.matchAll(new RegExp(`MacroAmount\\(value\\.${macro}Grams\\)`, "g"))].length, 2);
    }
    assert.doesNotMatch(ui, /decimal\.Round|Math\.Round/);
});

test("diary and nutrition cards consistently format macro grams as whole numbers", () => {
    for (const file of ["MainPage.Diary.cs", "MainPage.ProductCards.cs", "MainPage.DishCards.cs", "MainPage.PreviewCards.cs"]) {
        const content = source(file);
        for (const macro of ["Protein", "Fat", "Carbohydrates"]) {
            assert.match(content, new RegExp(`Ui\\.MacroAmount\\([^\\n]+\\.${macro}Grams\\)`), file);
            assert.doesNotMatch(content, new RegExp(`Ui\\.Amount\\([^\\n]+\\.${macro}Grams\\)`), file);
        }
    }
    const diary = source("MainPage.Diary.cs").split("private static View MacroTile(")[1].split("private View DiaryEntryRow(")[0];
    assert.match(diary, /Ui\.MacroAmount\(consumed\)/);
    assert.match(diary, /Ui\.MacroAmount\(goal\.Value\)/);
    assert.match(diary, /Ui\.MacroAmount\(consumed - goal\.Value\)/);
    assert.match(diary, /if \(excessAmount != "0"\)[\s\S]+values\.Children\.Add\(excess\)/);
    assert.doesNotMatch(diary, /Ui\.Amount\(/);
});

test("linked goal sliders show only whole grams in their value labels", () => {
    const goal = source("MainPage.Goal.cs");
    assert.match(goal, /control\.Value\.Text = valid \? \$"\{Ui\.MacroAmount\(grams\)\} г" : "—"/);
    assert.match(goal, /valid \? \$"\{control\.Name\}\. \{Ui\.MacroAmount\(grams\)\} граммов" : control\.Name/);
    assert.doesNotMatch(goal, /Ui\.Amount\(grams\)|процентов|\{percent[^}]*\}%/);
    assert.match(goal, /allocation\.WithShare\(macro,/);
    assert.match(goal, /new NutritionResponse\(values\.Calories, values\.ProteinGrams, values\.FatGrams, values\.CarbohydratesGrams\)/);
});

test("editable nutrition and portion masses retain their original decimal values", () => {
    const products = source("MainPage.Products.cs");
    for (const macro of ["Protein", "Fat", "Carbohydrates"]) {
        assert.match(products, new RegExp(`AddNumber\\([^\\n]+draft\\?\\.${macro}Grams[^\\n]+labelDraft\\?\\.${macro}Grams\\)`));
    }
    assert.match(source("MainPage.xaml.cs"), /Ui\.Field\("", value\?\.ToString\(CultureInfo\.CurrentCulture\), true\)/);
    assert.match(source("MainPage.Diary.cs"), /Ui\.Amount\(entry\.WeightInGrams\)/);
    assert.match(source("MainPage.DishCards.cs"), /Ui\.Amount\(dish\.FinalWeightInGrams\)/);
    assert.match(source("MainPage.PreviewCards.cs"), /PreviewWeight\(decimal\? weight\)[^\n]+Ui\.Amount\(grams\)/);
    assert.doesNotMatch(products, /Ui\.MacroAmount\(/);
});

test("native text inputs do not override Android TalkBack edit actions", () => {
    assert.doesNotMatch(source("MainPage.InputLayout.cs"), /SemanticProperties\.SetDescription\(editor,/);
    assert.doesNotMatch(source("MainPage.Goal.cs"), /SemanticProperties\.SetDescription\(calories,/);
    const numberField = source("MainPage.xaml.cs").split("private static Entry AddNumber(")[1];
    assert.doesNotMatch(numberField, /SemanticProperties\.SetDescription\(field,/);
    assert.match(source("MainPage.xaml.cs"), /page\.Fields\.Children\.Add\(Ui\.Text\(label\)\)/);
    assert.match(source("MainPage.Goal.cs"), /Ui\.Text\("Калории, ккал"\)/);
    assert.match(source("MainPage.Input.cs"), /editor\.Placeholder =/);
});
