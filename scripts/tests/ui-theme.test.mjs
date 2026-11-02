import test from "node:test";
import assert from "node:assert/strict";
import { applyTheme } from "../../NutriFlow.Ui/wwwroot/theme.mjs";

function createRoot() {
    const properties = new Map();
    return { properties, style: { setProperty: (name, value) => properties.set(name, value) }, dataset: {} };
}

test("theme variables go to the root through the cssom and the browser bar takes the background", () => {
    const root = createRoot();
    const meta = { content: "#151110", setAttribute(name, value) { this[name] = value; } };

    applyTheme("taiga", { "--background": "#17352A", "--accent": "#F7B95E" }, "#17352A", false, true, root, meta);

    assert.deepEqual([...root.properties], [["--background", "#17352A"], ["--accent", "#F7B95E"]]);
    assert.equal(root.dataset.theme, "taiga");
    assert.equal("flatNav" in root.dataset, false);
    assert.equal(meta.content, "#17352A");
    assert.equal(root.style.colorScheme, "dark");
});

test("a navigation in the page colour is marked for its hairline and unmarked by the next coloured theme", () => {
    const root = createRoot();

    applyTheme("light", { "--background": "#F5F5F6" }, "#F5F5F6", true, false, root, null);
    assert.equal(root.style.colorScheme, "light");
    assert.equal(root.dataset.flatNav, "");
    assert.equal(root.properties.get("--background"), "#F5F5F6");

    applyTheme("indigo", { "--background": "#2F2873" }, "#2F2873", false, true, root, null);
    assert.equal("flatNav" in root.dataset, false);
});
