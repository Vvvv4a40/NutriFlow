import test from "node:test";
import assert from "node:assert/strict";
import { back, enterLayer, fixedLayoutHeight, focus, keyboardInset, leaveLayer, push, setInputBlocked, start } from "../../NutriFlow.Ui/wwwroot/navigation.mjs";
import { close, isOutside, open } from "../../NutriFlow.Ui/wwwroot/dialog.mjs";

function element(tagName, { reachable = false } = {}) {
    return { tagName, inert: false, children: [], parentElement: null, matches: () => reachable };
}

function append(parent, ...children) {
    for (const child of children) {
        child.parentElement = parent;
        parent.children.push(child);
    }

    return parent;
}

function control(name, log) {
    return { name, isConnected: true, closest: () => null, focus() { log.push(name); } };
}

function page() {
    const body = element("BODY");
    const app = element("DIV");
    const shell = element("DIV");
    const header = element("HEADER");
    const main = element("MAIN");
    const content = element("SECTION");
    const bottom = element("DIV");
    const live = element("DIV", { reachable: true });
    const dialog = element("DIALOG");
    const errorUi = element("DIV", { reachable: true });
    const template = element("TEMPLATE");
    append(main, content);
    append(shell, header, main, bottom);
    append(app, shell, live, dialog);
    append(body, app, errorUi, template);
    return { body, app, shell, header, main, content, bottom, live, dialog, errorUi, template };
}

test("every back step of the browser reaches the back stack in .NET", async () => {
    const calls = [];
    const target = new EventTarget();
    start({ invokeMethodAsync: name => { calls.push(name); return Promise.resolve(); } }, target, null);

    target.dispatchEvent(new Event("popstate"));
    target.dispatchEvent(new Event("popstate"));

    assert.deepEqual(calls, ["OnPopStateAsync", "OnPopStateAsync"]);
});

test("layers add same-address entries and leave through the browser back", () => {
    const history = {
        steps: [],
        pushState(state, title) { this.steps.push(["push", state, title]); },
        back() { this.steps.push(["back"]); }
    };

    push(history);
    back(history);

    assert.deepEqual(history.steps, [["push", { nutriflow: true }, ""], ["back"]]);
});

test("focus moves into a new layer without scrolling the page", () => {
    const options = [];
    focus({ focus: value => options.push(value) });
    focus(null);

    assert.deepEqual(options, [{ preventScroll: true }]);
});

test("a layer next to the shell makes the rest of the page inert but keeps the live region, dialogs and alerts", () => {
    const focused = [];
    const dom = page();
    const form = element("DIV");
    append(dom.app, form);

    enterLayer("form-a", form, control("open-form", focused));
    assert.equal(dom.shell.inert, true);
    assert.equal(dom.template.inert, true);
    assert.equal(dom.live.inert, false);
    assert.equal(dom.dialog.inert, false);
    assert.equal(dom.errorUi.inert, false);

    leaveLayer("form-a");
    leaveLayer("form-a");
    assert.equal(dom.shell.inert, false);
    assert.equal(dom.template.inert, false);
    assert.deepEqual(focused, ["open-form"]);
});

test("a layer rendered inside the shell also takes the header and the navigation out of reach", () => {
    const dom = page();
    const form = element("DIV");
    append(dom.main, form);

    enterLayer("inside", form, null);
    assert.deepEqual([dom.content, dom.header, dom.bottom, dom.shell].map(node => node.inert), [true, true, true, false]);

    leaveLayer("inside");
    assert.deepEqual([dom.content, dom.header, dom.bottom].map(node => node.inert), [false, false, false]);
});

test("a nested layer leaves the shell to the first layer and returns the focus inside it", () => {
    const focused = [];
    const dom = page();
    const first = element("DIV");
    append(dom.app, first);
    enterLayer("first", first, control("open-first", focused));
    const second = element("DIV");
    append(dom.app, second);
    enterLayer("second", second, control("open-second", focused));
    assert.equal(first.inert, true);

    leaveLayer("second");
    assert.equal(first.inert, false);
    assert.equal(dom.shell.inert, true);
    leaveLayer("first");
    assert.equal(dom.shell.inert, false);
    assert.deepEqual(focused, ["open-second", "open-first"]);
});

test("a removed or hidden opener does not take the focus", () => {
    const focused = [];
    const gone = { ...control("gone", focused), isConnected: false };
    const hidden = { ...control("hidden", focused), closest: () => ({}) };

    const first = page();
    const a = element("DIV");
    append(first.app, a);
    enterLayer("a", a, gone);
    leaveLayer("a");
    const second = page();
    const b = element("DIV");
    append(second.app, b);
    enterLayer("b", b, hidden);
    leaveLayer("b");

    assert.deepEqual(focused, []);
});

test("while an action runs, clicks and enter or space are dropped outside the dialog it opened", () => {
    const listeners = new Map();
    const doc = {
        addEventListener: (type, listener, capture) => listeners.set(type, { listener, capture }),
        removeEventListener: type => listeners.delete(type)
    };
    const event = (type, key, inDialog) => ({
        type, key, prevented: false, stopped: false,
        target: { closest: selector => (inDialog && selector === "dialog[open]" ? {} : null) },
        preventDefault() { this.prevented = true; },
        stopPropagation() { this.stopped = true; }
    });

    setInputBlocked(true, doc);
    assert.equal(listeners.get("click").capture, true);
    const click = event("click", undefined, false);
    const enter = event("keydown", "Enter", false);
    const letter = event("keydown", "a", false);
    const dialogClick = event("click", undefined, true);
    for (const item of [click, enter, letter, dialogClick]) listeners.get(item.type).listener(item);

    assert.deepEqual([click, enter, letter, dialogClick].map(item => item.prevented && item.stopped), [true, true, false, false]);
    setInputBlocked(false, doc);
    assert.equal(listeners.size, 0);
});

test("the keyboard inset is the part of the layout viewport the on-screen keyboard covers", () => {
    assert.equal(keyboardInset({ height: 480, offsetTop: 0 }, 812), 332);
    assert.equal(keyboardInset({ height: 480, offsetTop: 40 }, 812), 292);
    assert.equal(keyboardInset({ height: 900, offsetTop: 0 }, 812), 0);
    assert.equal(keyboardInset(undefined, 812), 0);
});

test("the height of a fixed element counts, not window.innerHeight that shrinks with the keyboard on some ios versions", () => {
    const probe = { getBoundingClientRect: () => ({ height: 812 }) };
    const layout = fixedLayoutHeight(probe, { innerHeight: 480 });

    assert.equal(layout, 812);
    assert.equal(keyboardInset({ height: 480, offsetTop: 0 }, layout), 332);
    assert.equal(fixedLayoutHeight(undefined, { innerHeight: 700 }), 700);
});

test("escape on a dialog is turned into its cancel button and the dialog opens modal once", () => {
    const calls = [];
    const dialog = Object.assign(new EventTarget(), {
        open: false,
        classList: { contains: () => false },
        showModal() { this.open = true; calls.push("showModal"); },
        close() { this.open = false; calls.push("close"); }
    });
    const host = { invokeMethodAsync: name => { calls.push(name); return Promise.resolve(); } };

    open(dialog, host);
    open(dialog, host);
    const escape = new Event("cancel", { cancelable: true });
    dialog.dispatchEvent(escape);

    assert.equal(escape.defaultPrevented, true);
    assert.deepEqual(calls, ["showModal", "CancelAsync"]);

    close(dialog);
    close(dialog);
    assert.deepEqual(calls, ["showModal", "CancelAsync", "close"]);
});

test("a tap on the dimmed page cancels an action sheet, a tap inside it does not", () => {
    const calls = [];
    const box = { left: 0, right: 375, top: 500, bottom: 812 };
    const sheet = Object.assign(new EventTarget(), {
        open: false,
        classList: { contains: name => name === "nf-dialog--sheet" },
        getBoundingClientRect: () => box,
        showModal() { this.open = true; }
    });
    open(sheet, { invokeMethodAsync: name => { calls.push(name); return Promise.resolve(); } });

    const tap = (x, y) => {
        const event = new Event("click");
        Object.defineProperties(event, { clientX: { value: x }, clientY: { value: y }, target: { value: sheet } });
        sheet.dispatchEvent(event);
    };
    tap(180, 300);
    tap(180, 600);

    assert.deepEqual(calls, ["CancelAsync"]);
    assert.equal(isOutside(box, { clientX: 10, clientY: 520 }), false);
});
