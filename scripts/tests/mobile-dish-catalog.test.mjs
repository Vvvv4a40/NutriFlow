import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const mobile = new URL("../../mobile/NutriFlow.Mobile/", import.meta.url);
const source = name => readFileSync(new URL(name, mobile), "utf8");
const dishes = source("MainPage.Dishes.cs");
const catalog = dishes.split("private async Task RenderDishesAsync(Guid profileId)")[1].split("private async Task DishDetailPageAsync")[0];
const detail = dishes.split("private async Task DishDetailPageAsync(")[1].split("private async Task ManualDishPageAsync")[0];
const cards = source("MainPage.DishCards.cs");

test("dish catalog keeps a short heading and a clearly named manual creation action", () => {
    assert.match(catalog, /Ui\.Title\("Блюда"\)/);
    assert.match(catalog, /SemanticProperties\.SetHeadingLevel\(title, SemanticHeadingLevel\.Level1\)/);
    assert.match(catalog, /Ui\.Action\("Собрать блюдо", \(\) => ManualDishPageAsync\(profileId\), true\)/);
    assert.match(catalog, /manual\.AutomationId = "dish-create-manual"/);
    assert.match(catalog, /await _client\.GetSavedDishesAsync\(profileId\)/);
    assert.doesNotMatch(catalog, /Ваши блюда|Сохранённый состав и КБЖУ|Собрать блюдо вручную/);
});

test("empty dish catalog retains its native marker without instructional paragraphs", () => {
    assert.match(catalog, /Ui\.Title\("Нет блюд"\)/);
    assert.match(catalog, /SemanticProperties\.SetHeadingLevel\(emptyTitle, SemanticHeadingLevel\.Level2\)/);
    assert.match(catalog, /Border empty = Ui\.Card\(emptyTitle\)/);
    assert.match(catalog, /empty\.AutomationId = "dish-catalog-empty"/);
    assert.match(catalog, /_body\.Children\.Add\(empty\);\s*return;/);
    assert.doesNotMatch(catalog, /Пока нет блюд|Соберите состав|После проверки и подтверждения|локальном профиле|своими словами/);
});

test("dish catalog only shows a result count after returning from its empty branch", () => {
    const emptyStart = catalog.indexOf("if (dishes.Count == 0)");
    const emptyReturn = catalog.indexOf("return;", emptyStart);
    const countLabel = catalog.indexOf("Label count");
    assert.ok(emptyStart >= 0 && emptyReturn > emptyStart && countLabel > emptyReturn,
        "An empty dish catalog must not display a redundant zero count");
    assert.match(catalog, /count\.AutomationId = "dish-results-count"/);
    assert.match(catalog, /foreach \(SavedDishResponse dish in dishes\)[\s\S]*SavedDishCard\(profileId, dish\)/);
});

test("compact dish cards keep a wrapping name and explicit nutrition basis beside calories", () => {
    assert.match(cards, /Ui\.Title\(dish\.Name\)/);
    assert.match(cards, /name\.LineBreakMode = LineBreakMode\.WordWrap/);
    assert.match(cards, /SemanticProperties\.SetHeadingLevel\(name, SemanticHeadingLevel\.Level2\)/);
    assert.match(cards, /NutritionResponse values = dish\.NutritionPer100Grams/);
    assert.match(cards, /Ui\.Amount\(values\.Calories\)[^\n]+ккал[^\n]+на 100 г/);
    assert.doesNotMatch(cards, /Пищевая ценность на 100 г/);
});

test("dish cards retain whole macro grams and visible quality without verbose labels", () => {
    for (const macro of ["Protein", "Fat", "Carbohydrates"]) {
        assert.match(cards, new RegExp(`Ui\\.MacroAmount\\(values\\.${macro}Grams\\)`));
    }
    assert.match(cards, /\.Children\.Add\([^\n]*Ui\.Quality\(dish\.NutritionQuality\)/);
    assert.doesNotMatch(cards, /Качество КБЖУ:/);
});

test("dish card opens the exact selected dish and profile through its stable native IDs", () => {
    assert.match(cards, /Ui\.Action\("Состав и порция", \(\) => DishDetailPageAsync\(profileId, dish\), true\)/);
    assert.match(cards, /open\.AutomationId = \$"saved-dish-\{dish\.Id:N\}-open"/);
    assert.match(cards, /card\.AutomationId = \$"saved-dish-\{dish\.Id:N\}"/);
    assert.match(cards, /SetDescription\(open, \$"Состав и порция блюда «\{dish\.Name\}»"\)/);
    assert.doesNotMatch(cards, /CreateSavedDishMealAsync|ConfirmAsync/);
});

test("saved weight moves to dish detail without changing the guarded portion draft workflow", () => {
    assert.doesNotMatch(cards, /FinalWeightInGrams|Вес при сохранении/);
    assert.match(detail, /Ui\.Amount\(savedDish\.FinalWeightInGrams\)/);
    assert.match(detail, /FindSavedDishAsync\(profileId, savedDish\.Id\)/);
    assert.match(detail, /DateOnly mealDate = _date/);
    assert.match(detail, /ingredient\.ResolvedProduct\.SourceName[^\n]+Ui\.Quality\(ingredient\.ResolvedProduct\.DataQuality\)/);
    assert.match(detail, /if \(!await CanStartDraftAsync\(\)\)/);
    assert.match(detail, /CreateSavedDishMealAsync\(profileId, savedDish\.Id, Ui\.Number\(grams, "Вес порции", true\), mealDate, estimated\.IsToggled \? DataQuality\.Estimated : DataQuality\.Exact\)/);
    assert.doesNotMatch(detail, /DatePicker|ConfirmAsync/);
});
