import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { existsSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { PassThrough } from "node:stream";
import { test } from "node:test";
import { testAndroidEmulator } from "../test-android-emulator.mjs";

const packageId = "com.nutriflow.app";
const serial = "emulator-5556";
const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=", "base64");

function node(text, extra = "") {
    return `<node text="${text}" package="${packageId}" class="android.widget.TextView" enabled="true" bounds="[20,200][300,250]" ${extra} />`;
}
function identifiedNode(text, id, extra = "") {
    return node(text, `resource-id="${id}" ${extra}`);
}
function welcomeNodes() {
    return identifiedNode("NutriFlow", "welcome-title") + identifiedNode("НАЧАТЬ", "create-profile", 'clickable="true"');
}
function formNodes(value = "Имя") {
    return node("Начало") + node("Твоё имя") + identifiedNode("Продолжить", "profile-create", 'clickable="true"') +
        identifiedNode(value, "profile-name").replace("android.widget.TextView", "android.widget.EditText");
}
function addAction() {
    return identifiedNode("+", "add-food", 'content-desc="Добавить еду" clickable="true"');
}
function diaryNodes(diaryAddAction = addAction()) {
    return identifiedNode("0", "diary-calories") + node("ккал") + node("Белки") + node("Жиры") + node("Углеводы") +
        node("Цель · внешняя") + node("Съедено · внутренняя") +
        node("Smoke", 'clickable="true"').replace("android.widget.TextView", "android.widget.EditText") +
        diaryAddAction;
}
function destinationNodes(destination) {
    const screens = {
        products: node("Продукты") + identifiedNode("Название продукта", "product-search")
            .replace("android.widget.TextView", "android.widget.EditText") +
            identifiedNode("Найти", "product-search-submit", 'clickable="true"'),
        dishes: node("Ваши блюда") + identifiedNode("Собрать блюдо вручную", "dish-create-manual", 'clickable="true"') +
            identifiedNode("Показано: 0", "dish-results-count"),
        input: identifiedNode("", "input-description").replace("android.widget.TextView", "android.widget.EditText") +
            identifiedNode("В дневник", "input-purpose-diary", 'clickable="true"') +
            identifiedNode("Создать блюдо", "input-purpose-dish", 'clickable="true"') +
            identifiedNode("Разобрать описание", "input-submit", 'clickable="true"').replace('enabled="true"', 'enabled="false"')
    };
    return screens[destination] + addAction();
}
function menuNodes(content = diaryNodes()) {
    return content.replace('text="+"', 'text="×"').replace('content-desc="Добавить еду"', 'content-desc="Закрыть меню добавления"') +
        identifiedNode("", "add-menu").replace('bounds="[20,200][300,250]"', 'bounds="[20,280][300,550]"') +
        [["Записать еду", "input", 300], ["Продукты", "products", 400], ["Мои блюда", "dishes", 500]]
            .map(([text, id, top]) => identifiedNode(text, `add-menu-${id}`, 'clickable="true"')
                .replace('bounds="[20,200][300,250]"', `bounds="[20,${top}][300,${top + 50}]"`)).join("");
}
function hierarchy(screen, diaryAddAction, screenOverrides = {}) {
    const diary = diaryNodes(diaryAddAction);
    const screens = {
        welcome: welcomeNodes(),
        form: formNodes(),
        filled: formNodes("Smoke"),
        diary,
        products: destinationNodes("products"),
        dishes: destinationNodes("dishes"),
        input: destinationNodes("input"),
        "menu-diary": menuNodes(diary),
        ...Object.fromEntries(["products", "dishes", "input"].map(destination =>
            [`menu-${destination}`, menuNodes(destinationNodes(destination))])),
        "busy-diary": diary + node("Выполняется…"),
        "disabled-diary": diary.replaceAll('enabled="true"', 'enabled="false"'),
        launcher: node("Launcher").replace(packageId, "com.android.launcher3"),
        splash: node("NutriFlow"),
        ...screenOverrides
    };
    return `<?xml version="1.0"?><hierarchy rotation="0">${screens[screen]}</hierarchy>`;
}

function fixture(context, { change = () => undefined, startFailure = false, welcome = "welcome", persisted = true,
    consoleLineEnding = "\n", extraConsoleLine = "", diaryScreen = "diary", diarySequence = [], diaryAddAction,
    formBack = "welcome", keyboardBack = "filled", menuBack, menuDestinations = {}, destinationBack = "diary", screenOverrides = {} } = {}) {
    const runnerTemp = mkdtempSync(join(tmpdir(), "nutriflow-android-smoke-test-"));
    context.after(() => rmSync(runnerTemp, { recursive: true, force: true }));
    const apkPath = join(runnerTemp, "verified APK.apk");
    const outputDirectory = join(runnerTemp, "output with spaces");
    const sdkDirectory = join(runnerTemp, "Android SDK");
    mkdirSync(sdkDirectory);
    writeFileSync(apkPath, "fixture APK, not an actual Android package");
    const calls = [];
    const starts = [];
    const diarySnapshots = [];
    const screenshots = [];
    const child = new EventEmitter();
    Object.assign(child, { pid: startFailure ? undefined : 4000, exitCode: null, signalCode: null,
        stdout: new PassThrough(), stderr: new PassThrough(), signals: [], unref: () => {} });
    child.kill = signal => {
        child.signals.push(signal);
        queueMicrotask(() => { child.signalCode = signal; child.emit("exit", null, signal); });
        return true;
    };
    let avdName;
    let screen = welcome;
    let keyboardVisible = false;
    let launches = 0;
    let time = 0;
    function execute(command, args, options) {
        calls.push({ command, args, options });
        const changed = change(command, args, options);
        if (changed) return changed;
        let stdout = "";
        if (command.endsWith("avdmanager")) avdName = args[args.indexOf("-n") + 1];
        else if (command.endsWith("adb")) {
            if (args[0] === "devices") stdout = "List of devices attached\n";
            else {
                assert.deepEqual(args.slice(0, 2), ["-s", serial]);
                const operation = args.slice(2);
                if (operation[0] === "emu") stdout = `${avdName}${consoleLineEnding}OK${consoleLineEnding}${extraConsoleLine}`;
                else if (operation[0] === "install") stdout = "Success\n";
                else if (operation[0] === "exec-out") {
                    screenshots.push({ screen, diarySnapshots: [...diarySnapshots] });
                    stdout = png;
                }
                else if (operation[0] === "shell") {
                    if (operation[1] === "getprop") stdout = "1";
                    else if (operation[1] === "pidof") stdout = String(5000 + launches);
                    else if (operation[1] === "cmd") stdout = `priority=0 preferredOrder=0 match=0x108000\n${packageId}/crc123.MainActivity\n`;
                    else if (operation[1] === "am" && operation[2] === "start") {
                        launches++;
                        if (launches > 1) screen = persisted ? "diary" : "welcome";
                        stdout = "Starting: Intent {}\nStatus: ok\nActivity: com.nutriflow.app/crc123.MainActivity\n";
                    } else if (operation[1] === "cat") {
                        const snapshot = screen === "diary" ? diarySequence.shift() ?? diaryScreen : screen;
                        if (screen === "diary") diarySnapshots.push(snapshot);
                        stdout = hierarchy(snapshot, diaryAddAction, screenOverrides);
                    }
                    else if (operation[1] === "input" && operation[2] === "tap") {
                        const coordinates = operation.slice(3);
                        if (screen.startsWith("menu-")) {
                            assert.equal(coordinates[0], "160");
                            const destination = { 325: "input", 425: "products", 525: "dishes" }[coordinates[1]];
                            assert.ok(destination || coordinates[1] === "225");
                            screen = destination ? menuDestinations[destination] ?? destination : screen.slice(5);
                            return { status: 0, stdout, stderr: "" };
                        }
                        assert.deepEqual(coordinates, ["160", "225"]);
                        if (screen === "welcome") {
                            screen = "form";
                            keyboardVisible = false;
                        } else if (screen === "form") keyboardVisible = true;
                        else if (screen === "filled") {
                            screen = "diary";
                            keyboardVisible = false;
                        } else if (["diary", "products", "dishes", "input"].includes(screen)) screen = `menu-${screen}`;
                    } else if (operation[1] === "input" && operation[2] === "text") {
                        assert.equal(screen, "form");
                        assert.equal(operation[3], "Smoke");
                        assert.equal(keyboardVisible, true);
                        screen = "filled";
                    } else if (operation[1] === "input" && operation[2] === "keyevent" && operation[3] === "4") {
                        if (keyboardVisible) {
                            keyboardVisible = false;
                            screen = keyboardBack;
                        } else if (screen === "form") screen = formBack;
                        else if (screen.startsWith("menu-")) screen = menuBack ?? screen.slice(5);
                        else if (["products", "dishes", "input"].includes(screen)) screen = destinationBack;
                    }
                }
            }
        } else assert.ok(command.endsWith("emulator"));
        return { status: 0, stdout, stderr: "" };
    }
    return {
        runnerTemp, outputDirectory, apkPath, sdkDirectory, calls, starts, child, diarySnapshots, screenshots,
        run: options => testAndroidEmulator({ apkPath, outputDirectory, sdkDirectory, runnerTemp, execute,
            start: (command, args, settings) => {
                starts.push({ command, args, settings });
                queueMicrotask(() => child.emit(startFailure ? "error" : "spawn", ...(startFailure ? [new Error("emulator ENOENT")] : [])));
                return child;
            },
            pause: async milliseconds => { time += milliseconds; }, now: () => time, ...options }),
        report: () => JSON.parse(readFileSync(join(outputDirectory, "result.json"), "utf8"))
    };
}

test("installs and launches the selected emulator, creates a profile by UI bounds and verifies it after restart", async context => {
    const sample = fixture(context);
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.installation, "passed");
    assert.equal(report.launch, "passed");
    assert.equal(report.backNavigation.form, "passed");
    assert.deepEqual(report.profile, { name: "Smoke", creation: "passed", persistence: "passed" });
    assert.match(report.apkSha256, /^[a-f0-9]{64}$/);
    assert.equal(report.cleanup, "passed");
    assert.equal(sample.starts.length, 1);
    assert.equal(sample.starts[0].settings.detached, false);
    assert.ok(sample.starts[0].args.includes("-no-window"));
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
    assert.ok(!readdirSync(sample.runnerTemp).some(name => name.startsWith("nutriflow-android-emulator-")));
    for (const call of sample.calls) {
        assert.equal(call.options.shell, false);
        assert.ok(call.options.timeout > 0 && call.options.timeout <= 150_000);
    }
    const backCommands = sample.calls.filter(call => call.args.slice(2).join(" ") === "shell input keyevent 4");
    assert.equal(backCommands.length, 9);
    const inputIndex = sample.calls.findIndex(call => call.args.slice(2).join(" ") === "shell input text Smoke");
    const formBackIndex = sample.calls.indexOf(backCommands[0]);
    assert.ok(formBackIndex < inputIndex);
    assert.ok(sample.calls.indexOf(backCommands[1]) > inputIndex);
    const reopenIndex = sample.calls.findIndex((call, index) => index > formBackIndex && call.args.slice(2, 5).join(" ") === "shell input tap");
    assert.ok(reopenIndex > formBackIndex && reopenIndex < inputIndex);
    assert.equal(sample.calls.slice(formBackIndex + 1, reopenIndex)
        .filter(call => call.args.slice(2, 4).join(" ") === "shell cat").length, 2);
    for (const file of ["welcome.png", "profile-before-back.xml", "welcome-after-form-back.xml", "welcome-after-form-back.png",
        "diary.png", "diary-after-restart.png", "diary-after-restart.xml", "logcat.log"]) {
        assert.ok(existsSync(join(sample.outputDirectory, file)));
    }
});

test("accepts package-qualified automation resource IDs while retaining the expected visible text", async context => {
    const qualify = xml => xml.replaceAll(/resource-id="([^"]+)"/g, `resource-id="${packageId}:id/$1"`);
    const sample = fixture(context, { screenOverrides: {
        welcome: qualify(welcomeNodes()), form: qualify(formNodes()), filled: qualify(formNodes("Smoke")),
        diary: qualify(diaryNodes()), "menu-diary": qualify(menuNodes()),
        ...Object.fromEntries(["products", "dishes", "input"].flatMap(destination => [
            [destination, qualify(destinationNodes(destination))],
            [`menu-${destination}`, qualify(menuNodes(destinationNodes(destination)))]
        ]))
    } });
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.profile.persistence, "passed");
});

for (const [description, welcome] of [
    ["missing title ID", welcomeNodes().replace('resource-id="welcome-title"', '')],
    ["wrong title text", welcomeNodes().replace('text="NutriFlow"', 'text="Splash"')],
    ["missing start ID", welcomeNodes().replace('resource-id="create-profile"', '')],
    ["wrong start ID", welcomeNodes().replace('resource-id="create-profile"', 'resource-id="another-action"')],
    ["wrong start text", welcomeNodes().replace('text="НАЧАТЬ"', 'text="Другая команда"')],
    ["nonclickable start action", welcomeNodes().replace('clickable="true"', 'clickable="false"')],
    ["disabled start action", identifiedNode("NutriFlow", "welcome-title") +
        identifiedNode("Начать", "create-profile", 'clickable="true"').replace('enabled="true"', 'enabled="false"')]
]) {
    test(`rejects welcome with ${description} even when the app process is alive`, async context => {
        const sample = fixture(context, { screenOverrides: { welcome } });
        await assert.rejects(sample.run(), /expected NutriFlow screen.*welcome.xml/);
        assert.equal(sample.report().profile.creation, "not-tested");
        assert.ok(!sample.calls.some(call => call.args.slice(2, 5).join(" ") === "shell input tap"));
        assert.ok(!existsSync(join(sample.outputDirectory, "welcome.png")));
    });
}

for (const [description, form] of [
    ["wrong heading", formNodes().replace('text="Твоё имя"', 'text="Другой экран"')],
    ["missing name field ID", formNodes().replace('resource-id="profile-name"', '')],
    ["noneditable name field", formNodes().replace('class="android.widget.EditText"', 'class="android.widget.TextView"')],
    ["missing submit ID", formNodes().replace('resource-id="profile-create"', '')],
    ["wrong submit text", formNodes().replace('text="Продолжить"', 'text="Другая команда"')],
    ["nonclickable submit action", formNodes().replace('clickable="true"', 'clickable="false"')],
    ["disabled submit action", node("Начало") + node("Твоё имя") +
        identifiedNode("Продолжить", "profile-create", 'clickable="true"').replace('enabled="true"', 'enabled="false"') +
        identifiedNode("Имя", "profile-name").replace('class="android.widget.TextView"', 'class="android.widget.EditText"')],
    ["disabled name field", node("Начало") + node("Твоё имя") + identifiedNode("Продолжить", "profile-create", 'clickable="true"') +
        identifiedNode("Имя", "profile-name").replace('class="android.widget.TextView"', 'class="android.widget.EditText"')
            .replace('enabled="true"', 'enabled="false"')]
]) {
    test(`rejects the profile form with ${description} before entering text`, async context => {
        const sample = fixture(context, { screenOverrides: { form } });
        await assert.rejects(sample.run(), /expected NutriFlow screen.*profile-before-back.xml/);
        assert.equal(sample.report().backNavigation.form, "not-tested");
        assert.equal(sample.report().profile.creation, "not-tested");
        assert.ok(!sample.calls.some(call => call.args.slice(2).join(" ") === "shell input text Smoke"));
    });
}

test("rejects a filled form when the entered text belongs to a different field", async context => {
    const sample = fixture(context, { screenOverrides: {
        filled: formNodes("Smoke").replace('resource-id="profile-name"', 'resource-id="another-field"')
    } });
    await assert.rejects(sample.run(), /expected NutriFlow screen.*profile-filled.xml/);
    assert.equal(sample.report().profile.creation, "not-tested");
    assert.ok(!existsSync(join(sample.outputDirectory, "diary.png")));
});

test("accepts the exact entered profile name with its native semantic description prefix", async context => {
    const sample = fixture(context, { screenOverrides: {
        filled: formNodes("Твоё имя, Smoke").replace('resource-id="profile-name"', 'resource-id="profile-name" hint="Имя"')
    } });
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.profile.creation, "passed");
    assert.equal(report.profile.persistence, "passed");
});

for (const value of ["Другое имя, Smoke", "Твоё имя, Smoke extra", "Твоё имя, Other", "Smoke extra"]) {
    test(`rejects a filled form with a different exact native value ${JSON.stringify(value)}`, async context => {
        const sample = fixture(context, { screenOverrides: { filled: formNodes(value) } });
        await assert.rejects(sample.run(), /expected NutriFlow screen.*profile-filled.xml/);
        assert.equal(sample.report().profile.creation, "not-tested");
        assert.ok(!existsSync(join(sample.outputDirectory, "diary.png")));
    });
}

for (const [description, formBack] of [["opens the launcher while the app process stays alive", "launcher"],
    ["leaves the form open", "form"]]) {
    test(`rejects system Back from a form when it ${description}`, async context => {
        const sample = fixture(context, { formBack });
        await assert.rejects(sample.run(), /expected NutriFlow screen.*welcome-after-form-back.xml/);
        const report = sample.report();
        assert.equal(report.phase, "form-back");
        assert.equal(report.backNavigation.form, "not-tested");
        assert.equal(report.profile.creation, "not-tested");
        assert.equal(report.pid, 5001);
        if (formBack === "launcher") assert.match(readFileSync(join(sample.outputDirectory, "welcome-after-form-back.xml"), "utf8"), /com\.android\.launcher3/);
        assert.ok(!sample.calls.some(call => call.args.slice(2).join(" ") === "shell input text Smoke"));
        assert.ok(!existsSync(join(sample.outputDirectory, "welcome-after-form-back.png")));
        assert.deepEqual(sample.child.signals, ["SIGTERM"]);
    });
}

test("rejects keyboard Back when it pops the profile form instead of only dismissing the keyboard", async context => {
    const sample = fixture(context, { keyboardBack: "welcome" });
    await assert.rejects(sample.run(), /expected NutriFlow screen.*profile-filled.xml/);
    const report = sample.report();
    assert.equal(report.phase, "create-profile");
    assert.equal(report.backNavigation.form, "passed");
    assert.equal(report.profile.creation, "not-tested");
    assert.ok(!existsSync(join(sample.outputDirectory, "diary.png")));
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
});

test("accepts the diary icon-only add action through its accessible description after creation and restart", async context => {
    const sample = fixture(context, { diaryAddAction: identifiedNode("+", "add-food", 'content-desc="Добавить еду" clickable="true"') });
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.deepEqual(report.profile, { name: "Smoke", creation: "passed", persistence: "passed" });
    assert.ok(existsSync(join(sample.outputDirectory, "diary-after-restart.png")));
});

test("verifies add-menu toggling and Back dismissal before visiting every destination and returning to Diary", async context => {
    const sample = fixture(context);
    const report = await sample.run();
    assert.deepEqual(report.addMenu, {
        toggle: "passed", back: "passed", products: "passed", dishes: "passed", input: "passed"
    });
    for (const file of ["menu-open.xml", "add-menu.png", "diary-after-menu-toggle.xml", "diary-after-menu-back.xml",
        ...["products", "dishes", "input"].flatMap(destination => [`${destination}.xml`, `${destination}.png`,
            `menu-on-${destination}.xml`, `${destination}-after-menu-back.xml`, `diary-after-${destination}-back.xml`])]) {
        assert.ok(existsSync(join(sample.outputDirectory, file)), file);
    }
});

test("accepts a docked add menu while underlying destination controls are outside the visible hierarchy", async context => {
    const sample = fixture(context, { screenOverrides: Object.fromEntries(["products", "dishes", "input"]
        .map(destination => [`menu-${destination}`, menuNodes(addAction())])) });
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.deepEqual(report.addMenu, {
        toggle: "passed", back: "passed", products: "passed", dishes: "passed", input: "passed"
    });
});

for (const [description, menu] of [
    ["missing panel ID", menuNodes().replace('resource-id="add-menu"', '')],
    ["a panel from another package", menuNodes().replace(identifiedNode("", "add-menu")
        .replace('bounds="[20,200][300,250]"', 'bounds="[20,280][300,550]"'),
        identifiedNode("", "add-menu").replace(packageId, "com.example.other"))],
    ["a disabled panel", menuNodes().replace(identifiedNode("", "add-menu")
        .replace('bounds="[20,200][300,250]"', 'bounds="[20,280][300,550]"'),
        identifiedNode("", "add-menu").replace('enabled="true"', 'enabled="false"'))],
    ["a panel without usable bounds", menuNodes().replace('bounds="[20,280][300,550]"', 'bounds="[0,0][0,0]"')],
    ["a stale plus icon", menuNodes().replace('text="×"', 'text="+"')],
    ["the closed accessible description", menuNodes().replace('content-desc="Закрыть меню добавления"', 'content-desc="Добавить еду"')],
    ...["input", "products", "dishes"].flatMap(destination => {
        const menu = menuNodes();
        const action = menu.match(new RegExp(`<node[^>]+resource-id="add-menu-${destination}"[^>]+>`))[0];
        return [
            [`missing ${destination} action ID`, menu.replace(`resource-id="add-menu-${destination}"`, '')],
            [`wrong ${destination} action text`, menu.replace(action, action.replace(/text="[^"]*"/, 'text="Другое действие"'))],
            [`a ${destination} action from another package`, menu.replace(action, action.replace(packageId, "com.example.other"))],
            [`a disabled ${destination} action`, menu.replace(action, action.replace('enabled="true"', 'enabled="false"'))],
            [`a nonclickable ${destination} action`, menu.replace(action, action.replace('clickable="true"', 'clickable="false"'))],
            [`a ${destination} action without usable bounds`, menu.replace(action, action.replace(/bounds="[^"]*"/, 'bounds="[0,0][0,0]"'))]
        ];
    })
]) {
    test(`rejects the add menu with ${description}`, async context => {
        const sample = fixture(context, { screenOverrides: { "menu-diary": menu } });
        await assert.rejects(sample.run(), /expected NutriFlow screen.*menu-open.xml/);
        assert.equal(sample.report().addMenu.toggle, "not-tested");
        assert.ok(!existsSync(join(sample.outputDirectory, "add-menu.png")));
        assert.deepEqual(sample.child.signals, ["SIGTERM"]);
    });
}

for (const menuBack of ["launcher", "menu-diary"]) {
    test(`rejects menu Back that leaves ${menuBack} visible instead of the unchanged Diary`, async context => {
        const sample = fixture(context, { menuBack });
        await assert.rejects(sample.run(), /expected NutriFlow screen.*diary-after-menu-back.xml/);
        assert.equal(sample.report().addMenu.toggle, "passed");
        assert.equal(sample.report().addMenu.back, "not-tested");
    });
}

for (const destination of ["products", "dishes", "input"]) {
    test(`rejects the ${destination} menu action opening another destination`, async context => {
        const sample = fixture(context, { menuDestinations: { [destination]: destination === "products" ? "input" : "products" } });
        await assert.rejects(sample.run(), new RegExp(`expected NutriFlow screen.*${destination}\\.xml`));
        assert.equal(sample.report().addMenu[destination], "not-tested");
    });
    test(`rejects a lingering add menu on the ${destination} destination`, async context => {
        const lingering = menuNodes(destinationNodes(destination)).replace('text="×"', 'text="+"')
            .replace('content-desc="Закрыть меню добавления"', 'content-desc="Добавить еду"');
        const sample = fixture(context, { screenOverrides: { [destination]: lingering } });
        await assert.rejects(sample.run(), new RegExp(`expected NutriFlow screen.*${destination}\\.xml`));
        assert.equal(sample.report().addMenu[destination], "not-tested");
    });
}

test("rejects menu Back from a catalog that changes the current destination", async context => {
    const sample = fixture(context, { menuBack: "diary" });
    await assert.rejects(sample.run(), /expected NutriFlow screen.*products-after-menu-back.xml/);
    assert.equal(sample.report().addMenu.products, "not-tested");
});

test("rejects a second Back from a destination that leaves the app on the launcher", async context => {
    const sample = fixture(context, { destinationBack: "launcher" });
    await assert.rejects(sample.run(), /expected NutriFlow screen.*diary-after-products-back.xml/);
    assert.equal(sample.report().addMenu.products, "not-tested");
});

for (const [description, action] of [
    ["wrong closed icon", identifiedNode("×", "add-food", 'content-desc="Добавить еду" clickable="true"')],
    ["missing accessible description", identifiedNode("+", "add-food", 'clickable="true"')],
    ["wrong accessible description", identifiedNode("+", "add-food", 'content-desc="Другая команда" clickable="true"')],
    ["missing automation ID", node("+", 'content-desc="Добавить еду" clickable="true"')],
    ["wrong automation ID", identifiedNode("+", "another-action", 'content-desc="Добавить еду" clickable="true"')],
    ["disabled accessible action", identifiedNode("+", "add-food", 'content-desc="Добавить еду" clickable="true"').replace('enabled="true"', 'enabled="false"')],
    ["nonclickable accessible action", identifiedNode("+", "add-food", 'content-desc="Добавить еду" clickable="false"')]
]) {
    test(`rejects an icon-only diary add action with ${description}`, async context => {
        const sample = fixture(context, { diaryAddAction: action });
        await assert.rejects(sample.run(), /expected NutriFlow screen.*diary.xml/);
        assert.equal(sample.report().profile.creation, "not-tested");
        assert.ok(!existsSync(join(sample.outputDirectory, "diary.png")));
        assert.deepEqual(sample.child.signals, ["SIGTERM"]);
    });
}

test("accepts the compact diary without an accessible summary container or legacy headings", async context => {
    const diary = diaryNodes();
    assert.ok(!diary.includes("diary-summary"));
    assert.ok(!diary.includes("Дневник питания") && !diary.includes("Съедено за день"));
    const sample = fixture(context, { screenOverrides: { diary } });
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.profile.persistence, "passed");
});

for (const [description, diary] of [
    ["only legacy headings", node("Дневник питания") + node("Съедено за день") +
        node("Smoke", 'clickable="true"') + identifiedNode("Добавить еду", "add-food", 'clickable="true"')],
    ["missing calorie value", diaryNodes().replace(identifiedNode("0", "diary-calories"), "")],
    ["missing calorie ID", diaryNodes().replace('resource-id="diary-calories"', "")],
    ["wrong calorie ID", diaryNodes().replace('resource-id="diary-calories"', 'resource-id="another-calorie"')],
    ["nonempty initial calorie value", diaryNodes().replace('text="0"', 'text="1982"')],
    ["a nonlabel calorie value", diaryNodes().replace(identifiedNode("0", "diary-calories"),
        identifiedNode("0", "diary-calories").replace("android.widget.TextView", "android.widget.EditText"))],
    ["a calorie value in another package", diaryNodes().replace(identifiedNode("0", "diary-calories"),
        identifiedNode("0", "diary-calories").replace(packageId, "com.example.other"))],
    ...["ккал", "Белки", "Жиры", "Углеводы", "Цель · внешняя", "Съедено · внутренняя"].map(text =>
        [`missing ${JSON.stringify(text)}`, diaryNodes().replace(node(text), "")]),
    ["an add action ID without its expected text or description", diaryNodes(identifiedNode("+", "add-food", 'clickable="true"'))],
    ["a visible add action without its ID", diaryNodes(node("Добавить еду", 'clickable="true"'))]
]) {
    test(`rejects a compact diary with ${description}`, async context => {
        const sample = fixture(context, { screenOverrides: { diary } });
        await assert.rejects(sample.run(), /expected NutriFlow screen.*diary.xml/);
        assert.equal(sample.report().profile.creation, "not-tested");
        assert.equal(sample.report().profile.persistence, "not-tested");
        assert.ok(!existsSync(join(sample.outputDirectory, "diary.png")));
        assert.deepEqual(sample.child.signals, ["SIGTERM"]);
    });
}

test("rejects am start failure even when adb exits zero and still stops its owned emulator", async context => {
    const sample = fixture(context, { change: (command, args) => command.endsWith("adb") && args.includes("start") ?
        { status: 0, stdout: "Error: Activity class does not exist.\n", stderr: "" } : undefined });
    await assert.rejects(sample.run(), /successful NutriFlow activity launch/);
    assert.equal(sample.report().phase, "launch");
    assert.equal(sample.report().status, "failed");
    assert.equal(sample.report().profile.creation, "not-tested");
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
    assert.ok(existsSync(join(sample.outputDirectory, "failure.png")));
});

test("accepts the owned emulator console identity with actual CRLF line endings", async context => {
    const sample = fixture(context, { consoleLineEnding: "\r\n" });
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.equal(report.installation, "passed");
    assert.equal(report.profile.persistence, "passed");
});

test("rejects extra emulator console identity lines even when the owned name and OK are present", async context => {
    const sample = fixture(context, { consoleLineEnding: "\r\n", extraConsoleLine: "unexpected" });
    await assert.rejects(sample.run(), /does not belong/);
    assert.ok(!sample.calls.some(call => call.args.includes("install")));
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
});

test("an app process and splash alone do not pass the native welcome screen check", async context => {
    const sample = fixture(context, { welcome: "splash" });
    await assert.rejects(sample.run(), /expected NutriFlow screen.*welcome.xml/);
    assert.equal(sample.report().launch, undefined);
    assert.equal(sample.report().profile.creation, "not-tested");
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
});

test("fails when the profile and diary disappear after restarting", async context => {
    const sample = fixture(context, { persisted: false });
    await assert.rejects(sample.run(), /diary-after-restart.xml/);
    assert.equal(sample.report().profile.creation, "passed");
    assert.equal(sample.report().profile.persistence, "not-tested");
    assert.equal(sample.report().status, "failed");
});

test("does not accept a busy diary even when its content and enabled controls are already visible", async context => {
    const sample = fixture(context, { diaryScreen: "busy-diary" });
    await assert.rejects(sample.run(), /expected NutriFlow screen.*diary.xml/);
    assert.equal(sample.report().profile.creation, "not-tested");
    assert.ok(!existsSync(join(sample.outputDirectory, "diary.png")));
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
});

test("does not accept diary content while the profile and action controls are disabled", async context => {
    const sample = fixture(context, { diaryScreen: "disabled-diary" });
    await assert.rejects(sample.run(), /expected NutriFlow screen.*diary.xml/);
    assert.equal(sample.report().profile.creation, "not-tested");
    assert.ok(!existsSync(join(sample.outputDirectory, "diary.png")));
});

test("waits through a brief ready-to-busy transition and captures PNG only after two consecutive idle diary snapshots", async context => {
    const sample = fixture(context, { diarySequence: ["busy-diary", "diary", "busy-diary", "diary", "diary"] });
    const report = await sample.run();
    assert.equal(report.status, "passed");
    assert.deepEqual(sample.diarySnapshots.slice(0, 5), ["busy-diary", "diary", "busy-diary", "diary", "diary"]);
    assert.ok(sample.diarySnapshots.slice(5).every(screen => screen === "diary"));
    const captured = sample.screenshots.filter(item => item.screen === "diary");
    assert.deepEqual(captured[0].diarySnapshots, ["busy-diary", "diary", "busy-diary", "diary", "diary"]);
    assert.deepEqual(captured.at(-1).diarySnapshots, sample.diarySnapshots);
});

test("does not reuse or send device commands to an existing selected serial", async context => {
    const sample = fixture(context, { change: (command, args) => args[0] === "devices" ?
        { status: 0, stdout: `List of devices attached\n${serial}\tdevice\n`, stderr: "" } : undefined });
    await assert.rejects(sample.run(), /already exists/);
    assert.equal(sample.starts.length, 0);
    assert.equal(sample.calls.length, 1);
    assert.equal(sample.report().cleanup, "passed");
});

test("rejects a serial reporting another AVD and never installs or captures its screen", async context => {
    const sample = fixture(context, { change: (command, args) => args.includes("emu") ?
        { status: 0, stdout: "someone-elses-avd\nOK", stderr: "" } : undefined });
    await assert.rejects(sample.run(), /does not belong/);
    assert.ok(!sample.calls.some(call => call.args.includes("install") || call.args.includes("screencap") || call.args.includes("logcat")));
    assert.deepEqual(sample.child.signals, ["SIGTERM"]);
});

test("records emulator startup failure and cleans the temporary AVD directory", async context => {
    const sample = fixture(context, { startFailure: true });
    await assert.rejects(sample.run(), /emulator ENOENT/);
    assert.equal(sample.report().status, "failed");
    assert.equal(sample.report().cleanup, "passed");
    assert.ok(!sample.calls.some(call => call.args.includes("install")));
});

test("refuses to create diagnostic output outside the runner temporary directory", async context => {
    const sample = fixture(context);
    await assert.rejects(sample.run({ outputDirectory: join(sample.runnerTemp, "..", "outside-output") }), /inside RUNNER_TEMP/);
    assert.equal(sample.calls.length, 0);
});
