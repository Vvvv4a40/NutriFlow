import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

const scriptPath = fileURLToPath(new URL("../Test-MealWorkflow.ps1", import.meta.url));
const harness = String.raw`
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $env:NUTRIFLOW_COMMAND_TEST_SCRIPT, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'The workflow script has PowerShell syntax errors.' }
$assignments = @($ast.EndBlock.Statements | Where-Object {
    $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
    $_.Left.VariablePath.UserPath -eq 'dotnetPath'
})
if ($assignments.Count -ne 1) { throw 'Expected one dotnetPath assignment.' }
$fixture = ConvertFrom-Json -InputObject $env:NUTRIFLOW_COMMAND_TEST_FIXTURE

function Get-Command {
    [CmdletBinding()]
    param([string]$Name, [System.Management.Automation.CommandTypes]$CommandType)

    if ($Name -ne 'dotnet' -or $CommandType -ne 'Application' -or
        -not $PSBoundParameters.ContainsKey('ErrorAction') -or
        [string]$PSBoundParameters['ErrorAction'] -ne 'Stop') {
        throw 'Expected a terminating dotnet application lookup.'
    }
    if ($fixture.missingDotnet) {
        throw [System.Management.Automation.CommandNotFoundException]::new('Synthetic dotnet lookup failure.')
    }
    foreach ($source in $fixture.paths) { [pscustomobject]@{ Source = $source } }
}

$dotnetPath = & ([scriptblock]::Create($assignments[0].Right.Extent.Text))
if ($dotnetPath -isnot [string]) { throw 'Expected exactly one executable path.' }
$startInfo = [System.Diagnostics.ProcessStartInfo]::new($dotnetPath)
ConvertTo-Json -InputObject ([pscustomobject]@{
    path = $dotnetPath
    fileName = $startInfo.FileName
}) -Compress
`;

function resolveDotnet(paths, missingDotnet = false) {
    return spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", harness], {
        shell: false,
        encoding: "utf8",
        timeout: 15_000,
        maxBuffer: 64 * 1024,
        env: {
            ...process.env,
            NUTRIFLOW_COMMAND_TEST_SCRIPT: scriptPath,
            NUTRIFLOW_COMMAND_TEST_FIXTURE: JSON.stringify({ paths, missingDotnet })
        }
    });
}

function assertResolution(paths) {
    const result = resolveDotnet(paths);
    assert.ifError(result.error);
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(result.stdout), { path: paths[0], fileName: paths[0] });
}

test("resolves a single dotnet executable", () => {
    assertResolution(["/usr/share/dotnet/dotnet"]);
});

test("uses the first dotnet executable when Linux lookup returns multiple paths", () => {
    assertResolution(["/usr/share/dotnet/dotnet", "/usr/bin/dotnet", "/bin/dotnet"]);
});

test("preserves spaces in the executable filename", () => {
    assertResolution(["C:\\Program Files\\dotnet\\dotnet.exe", "C:\\other dotnet\\dotnet.exe"]);
});

test("propagates a missing dotnet executable error", () => {
    const result = resolveDotnet([], true);
    assert.ifError(result.error);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Synthetic dotnet lookup failure\./);
});
