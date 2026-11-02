import test from "node:test";
import assert from "node:assert/strict";
import { showPicker } from "../../NutriFlow.Ui/wwwroot/picker.mjs";

function input({ picker = true } = {}) {
    const calls = [];
    return {
        calls,
        showPicker() {
            if (!picker) throw new DOMException("not supported", "NotSupportedError");
            calls.push("picker");
        },
        focus() { calls.push("focus"); }
    };
}

test("the diary date opens the system picker", () => {
    const date = input();
    showPicker(date);
    assert.deepEqual(date.calls, ["picker"]);
});

test("without showPicker the date field gets the focus instead", () => {
    const date = input({ picker: false });
    showPicker(date);
    assert.deepEqual(date.calls, ["focus"]);
});
