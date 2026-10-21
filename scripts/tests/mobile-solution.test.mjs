import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { test } from "node:test";

const root = new URL("../../", import.meta.url);

function solutionProjects(name) {
    const solution = new URL(`mobile/${name}`, root);
    const xml = readFileSync(solution, "utf8");
    return new Set([...xml.matchAll(/<Project\b[^>]*\bPath="([^"]+)"/g)]
        .map(match => new URL(match[1].replaceAll("\\", "/"), solution).href));
}

test("native mobile solution explicitly includes its project reference graph for editing", () => {
    const projects = solutionProjects("NutriFlow.Mobile.slnx");
    assert.ok(projects.has(new URL("mobile/NutriFlow.Mobile/NutriFlow.Mobile.csproj", root).href));
    for (const project of projects) {
        const xml = readFileSync(new URL(project), "utf8");
        for (const match of xml.matchAll(/<ProjectReference\b[^>]*\bInclude="([^"]+)"/g)) {
            const reference = new URL(match[1].replaceAll("\\", "/"), project).href;
            assert.ok(projects.has(reference), `${project} is missing ${reference} from the solution`);
        }
    }
    for (const project of solutionProjects("NutriFlow.Mobile.Core.slnx")) {
        assert.ok(projects.has(project), `The native solution is missing ${project}`);
    }
    assert.ok(!projects.has(new URL("NutriFlow.Api/NutriFlow.Api.csproj", root).href));
});

test("offline core solution keeps its tests without requiring MAUI", () => {
    const projects = solutionProjects("NutriFlow.Mobile.Core.slnx");
    assert.ok(projects.has(new URL("mobile/NutriFlow.Mobile.Core.Tests/NutriFlow.Mobile.Core.Tests.csproj", root).href));
    for (const project of projects) {
        const xml = readFileSync(new URL(project), "utf8");
        assert.doesNotMatch(xml, /<UseMaui>\s*true\s*<\/UseMaui>/i);
        assert.doesNotMatch(xml, /<TargetFrameworks?>[^<]*(?:-android|-ios)/i);
    }
});

test("Android manifest keeps legacy Back compatibility for the pinned MAUI runtime", () => {
    const manifest = readFileSync(new URL("mobile/NutriFlow.Mobile/Platforms/Android/AndroidManifest.xml", root), "utf8");
    const application = /<application\b[^>]*>/.exec(manifest)?.[0];
    assert.ok(application, "The Android application element is missing");
    assert.match(application, /\bandroid:enableOnBackInvokedCallback="false"/);
    assert.doesNotMatch(manifest, /<activity\b[^>]*\bandroid:enableOnBackInvokedCallback="true"/);
});

test("local Android SDK example stays independently pinned from cloud CI", () => {
    const sdk = JSON.parse(readFileSync(new URL("mobile/global.example.json", root), "utf8"));
    const workflow = readFileSync(new URL(".github/workflows/android-apk.yml", root), "utf8");
    assert.equal(sdk.sdk.version, "10.0.401");
    assert.equal(sdk.sdk.rollForward, "disable");
    assert.match(workflow, /dotnet-version:\s*'10\.0\.400'/);
});

test("Android Debug source configuration uses JIT without changing iOS or Release", () => {
    const project = readFileSync(new URL("mobile/NutriFlow.Mobile/NutriFlow.Mobile.csproj", root), "utf8");
    const settings = [...project.matchAll(/<UseInterpreter\b([^>]*)>([^<]*)<\/UseInterpreter>/g)];
    assert.equal(settings.length, 1);
    assert.equal(settings[0][2].trim(), "false");
    assert.match(settings[0][1], /'\$\(Configuration\)' == 'Debug'/);
    assert.match(settings[0][1], /GetTargetPlatformIdentifier\('\$\(TargetFramework\)'\)\) == 'android'/);
});

test("Android Release trimming keeps the locked Android runtime list", () => {
    const project = readFileSync(new URL("mobile/NutriFlow.Mobile/NutriFlow.Mobile.csproj", root), "utf8");
    const settings = [...project.matchAll(/<UseDefaultPublishRuntimeIdentifier\b([^>]*)>([^<]*)<\/UseDefaultPublishRuntimeIdentifier>/g)];
    assert.equal(settings.length, 1);
    assert.equal(settings[0][2].trim(), "false");
    assert.match(settings[0][1], /GetTargetPlatformIdentifier\('\$\(TargetFramework\)'\)\) == 'android'/);
    assert.match(project, /<RuntimeIdentifiers\b[^>]*>android-arm;android-arm64;android-x64<\/RuntimeIdentifiers>/);
});
