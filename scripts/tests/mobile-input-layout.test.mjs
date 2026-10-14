import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const mobile = new URL("../../mobile/NutriFlow.Mobile/", import.meta.url);

test("native input source configuration bounds the main editor and preserves its input contract", () => {
    const input = readFileSync(new URL("MainPage.Input.cs", mobile), "utf8");
    const editor = /Editor\s+editor\s*=\s*new\s+InputEditor\(\)\s*\{([\s\S]*?)\}\s*;/.exec(input)?.[1];
    assert.ok(editor, "The main native input editor configuration is missing");
    assert.match(editor, /\bAutoSize\s*=\s*EditorAutoSizeOption\.Disabled\b/);
    const height = /\bHeightRequest\s*=\s*(\d+(?:\.\d+)?)\s*,/.exec(editor)?.[1];
    assert.ok(height && Number.isFinite(Number(height)) && Number(height) > 0, "The editor needs a finite positive height");
    assert.equal(Number(height), 120);
    assert.match(editor, /\bMaxLength\s*=\s*4000\b/);
    assert.match(editor, /\bAutomationId\s*=\s*"input-description"/);
    assert.match(editor, /\bText\s*=\s*_draftText\b/);
    assert.match(input, /\bsubmit\.AutomationId\s*=\s*"input-submit"/);
});

test("native editor source configuration scopes touch arbitration and cleans up its handler", () => {
    const editor = readFileSync(new URL("InputEditor.cs", mobile), "utf8");
    assert.match(editor, /internal sealed class InputEditor\s*:\s*Editor/);
    assert.match(editor, /#if ANDROID[\s\S]*#endif/);
    assert.match(editor, /OnHandlerChanged\(\)[\s\S]*nativeEditor\.Touch \+= OnNativeTouch/);
    assert.match(editor, /OnHandlerChanging\(HandlerChangingEventArgs args\)[\s\S]*args\.OldHandler[\s\S]*nativeEditor\.Touch -= OnNativeTouch/);
    const down = /if\s*\(args\.Event\?\.ActionMasked == Android\.Views\.MotionEventActions\.Down\)\s*\{([^}]+)\}/.exec(editor)?.[1];
    assert.ok(down, "Touch ownership must be decided before a parent intercepts MOVE");
    assert.match(down, /CanScrollVertically\(-1\)\s*\|\|\s*nativeEditor\.CanScrollVertically\(1\)/);
    assert.match(down, /RequestDisallowInterceptTouchEvent\(canScroll\)/);
    assert.match(editor, /MotionEventActions\.Up or Android\.Views\.MotionEventActions\.Cancel[\s\S]*RequestDisallowInterceptTouchEvent\(false\)/);
    assert.match(editor, /args\.Handled\s*=\s*false;/);
});

test("native page source configuration has one Android-only keyboard-safe area owner", () => {
    const page = readFileSync(new URL("MainPage.xaml.cs", mobile), "utf8");
    const assignments = [...page.matchAll(/\bSafeAreaEdges\s*=\s*SafeAreaEdges\.\w+\s*;/g)];
    assert.equal(assignments.length, 1);
    assert.match(page, /^#if ANDROID\s*\r?\n[ \t]*SafeAreaEdges\s*=\s*SafeAreaEdges\.All;\s*\r?\n#endif\b/m);
});
