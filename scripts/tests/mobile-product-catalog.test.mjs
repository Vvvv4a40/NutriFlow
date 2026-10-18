import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const mobile = new URL("../../mobile/NutriFlow.Mobile/", import.meta.url);
const source = name => readFileSync(new URL(name, mobile), "utf8");
const products = source("MainPage.Products.cs");
const catalog = products.split("private async Task RenderProductsAsync(Guid profileId)")[1].split("private void RenderProductList")[0];
const list = products.split("private void RenderProductList(")[1].split("private Task ProductPortionPageAsync")[0];
const cards = source("MainPage.ProductCards.cs");

test("product catalog keeps one short heading and a persistent native search label", () => {
    assert.match(catalog, /Ui\.Title\("Продукты"\)/);
    assert.match(catalog, /SemanticProperties\.SetHeadingLevel\(title, SemanticHeadingLevel\.Level1\)/);
    assert.match(catalog, /Ui\.Text\("Поиск"\)/);
    assert.match(catalog, /Entry search = Ui\.Field\("Название продукта"\)/);
    assert.match(catalog, /search\.AutomationId = "product-search"/);
    assert.match(catalog, /search\.ReturnType = ReturnType\.Search/);
    assert.match(catalog, /search\.MaxLength = 200/);
    assert.doesNotMatch(catalog, /Сохранённая пищевая ценность|Поиск среди сохранённых продуктов|Поиск по названию|Например, овсянка/);
});

test("product search preserves explicit submission, query trimming and clear-to-all behavior", () => {
    assert.match(catalog, /Task FindProductsAsync\(\) => RunAsync\(async \(\) =>/);
    assert.match(catalog, /string query = search\.Text\?\.Trim\(\) \?\? ""/);
    assert.match(catalog, /RenderProductList\(results, profileId, await _client\.SearchProductsAsync\(profileId, query\), query\)/);
    assert.match(catalog, /Ui\.Action\("Найти", FindProductsAsync\)/);
    assert.match(catalog, /find\.AutomationId = "product-search-submit"/);
    assert.match(catalog, /Ui\.Action\("Сбросить", async \(\) =>\s*\{\s*search\.Text = "";\s*await FindProductsAsync\(\);/);
    assert.match(catalog, /clear\.AutomationId = "product-search-clear"/);
    assert.match(catalog, /search\.Completed \+= async \(_, _\) => await FindProductsAsync\(\)/);
    assert.match(catalog, /RenderProductList\(results, profileId, await _client\.SearchProductsAsync\(profileId\)\)/);
    assert.doesNotMatch(catalog, /search\.TextChanged/);
});

test("product addition keeps all three methods in one initially collapsed column", () => {
    assert.match(catalog, /Ui\.Action\("Вручную", \(\) => ProductEditorPageAsync\(profileId\)/);
    assert.match(catalog, /Ui\.Action\("Фото этикетки", \(\) => LabelSourcePageAsync\(profileId\)/);
    assert.match(catalog, /Ui\.Action\("Штрихкод", \(\) => BarcodePageAsync\(profileId\)/);
    for (const id of ["product-add-manual", "product-add-photo", "product-add-barcode", "product-add-methods", "product-add-options"]) {
        assert.ok(catalog.includes(`AutomationId = "${id}"`), id);
    }
    assert.match(catalog, /VerticalStackLayout addOptions = new\(\)\s*\{[^}]*IsVisible = false[^}]*\}/);
    assert.match(catalog, /addOptions\.IsVisible = !addOptions\.IsVisible/);
    assert.doesNotMatch(catalog, /Ui\.Row\(photo, barcode\)|Пополнить каталог|Groq|офлайн|без интернета|требует интернета/);
});

test("product list separates empty catalog and empty search without instructional paragraphs", () => {
    assert.match(list, /bool filtered = !string\.IsNullOrWhiteSpace\(query\)/);
    assert.match(list, /Ui\.Title\(filtered \? "Ничего не найдено" : "Нет продуктов"\)/);
    assert.match(list, /empty\.AutomationId = filtered \? "product-search-empty" : "product-catalog-empty"/);
    assert.match(list, /target\.Children\.Add\(empty\);\s*return;/);
    assert.match(list, /AutomationId = "product-results-count"/);
    const emptyStart = list.indexOf("if (products.Count == 0)");
    const emptyReturn = list.indexOf("return;", emptyStart);
    const countLabel = list.indexOf("Label count");
    const guardedCount = /if \(products\.Count > 0\)\s*\{\s*Label count/.test(list);
    assert.ok(guardedCount || (emptyStart >= 0 && countLabel > emptyReturn), "An empty list must not display a redundant zero count");
    assert.doesNotMatch(list, /Попробуйте другое|Добавьте первый|локальном профиле|Ui\.Text\(filtered/);
    assert.match(list, /foreach \(ProductResponse product in products\)[\s\S]*ProductCatalogCard\(profileId, product\)/);
});

test("compact product cards retain nutrition basis, whole macro grams and visible data quality", () => {
    assert.match(cards, /Ui\.Title\(product\.Name\)/);
    assert.match(cards, /name\.LineBreakMode = LineBreakMode\.WordWrap/);
    assert.match(cards, /SemanticProperties\.SetHeadingLevel\(name, SemanticHeadingLevel\.Level2\)/);
    assert.match(cards, /Ui\.Amount\(product\.Calories\)[^\n]+ккал[^\n]+на 100 г/);
    for (const macro of ["Protein", "Fat", "Carbohydrates"]) {
        assert.match(cards, new RegExp(`Ui\\.MacroAmount\\(product\\.${macro}Grams\\)`));
    }
    assert.match(cards, /content\.Children\.Add\([^\n]*Ui\.Quality\(product\.DataQuality\)[^\n]*sourceKind/);
    assert.doesNotMatch(cards, /Пищевая ценность на 100 г|Качество КБЖУ:/);
});

test("product source details start hidden but preserve full provenance and wrapping", () => {
    const details = /VerticalStackLayout\s+(\w+) = new\(\)\s*\{[^}]*IsVisible = false[^}]*\}/.exec(cards)?.[1];
    assert.ok(details, "The full source details should start collapsed");
    assert.match(cards, /string\.IsNullOrWhiteSpace\(product\.SourceKind\) \? "тип неизвестен" : Ui\.Source\(product\.SourceKind\)/);
    assert.match(cards, /string\.IsNullOrWhiteSpace\(product\.SourceName\) \? "название неизвестно" : product\.SourceName/);
    assert.match(cards, new RegExp(`${details}\\.Children\\.Add\\([^\\n]*sourceKind[^\\n]*sourceName`));
    for (const field of ["Barcode", "SourceReference"]) {
        assert.match(cards, new RegExp(`if \\(!string\\.IsNullOrWhiteSpace\\(product\\.${field}\\)\\)`));
        assert.match(cards, new RegExp(`ProductCatalogDetail\\([^\\n]*product\\.${field}`));
    }
    assert.ok([...cards.matchAll(/LineBreakMode = LineBreakMode\.CharacterWrap/g)].length >= 2);
    assert.match(cards, /Ui\.Action\("Источник",/);
    assert.match(cards, new RegExp(`${details}\\.IsVisible = !${details}\\.IsVisible`));
    assert.match(cards, new RegExp(`content\\.Children\\.Add\\(${details}\\)`));
});

test("source disclosure and add-portion share an action row without changing the selected product", () => {
    assert.match(cards, /Ui\.Action\("Добавить порцию", \(\) => ProductPortionPageAsync\(profileId, product\)/);
    assert.match(cards, /SetDescription\(add, \$"Добавить порцию продукта «\{product\.Name\}» в дневник"\)/);
    assert.match(cards, /content\.Children\.Add\(Ui\.Row\([^\n]*add[^\n]*\)\)/);
    const portion = products.split("private Task ProductPortionPageAsync(")[1].split("private Task ProductEditorPageAsync")[0];
    assert.match(portion, /DateOnly mealDate = _date/);
    assert.match(portion, /if \(!await CanStartDraftAsync\(\)\)/);
    assert.match(portion, /CreateManualMealAsync\(profileId, product, Ui\.Number\(grams, "Вес", true\), mealDate,/);
    assert.match(portion, /estimated\.IsToggled \? NutriFlow\.Domain\.DataQuality\.Estimated : NutriFlow\.Domain\.DataQuality\.Exact/);
    assert.doesNotMatch(portion, /DatePicker|ConfirmAsync/);
});
