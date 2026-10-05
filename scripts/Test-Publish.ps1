#Requires -Version 7.2

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$fixtureRoot = Join-Path $temporaryRoot "nutriflow-publish-$([guid]::NewGuid().ToString('N'))"
$sourceRoot = Join-Path $fixtureRoot 'source'
$publishRoot = Join-Path $fixtureRoot 'publish'
$succeeded = $false

if (Test-Path -LiteralPath $fixtureRoot) {
    throw 'The publish fixture directory already exists.'
}

[System.IO.Directory]::CreateDirectory($sourceRoot) | Out-Null

try {
    $trackedFiles = @(git -C $repositoryRoot -c core.quotepath=false ls-files -- NutriFlow.Api NutriFlow.Domain NutriFlow.Infrastructure)

    if ($LASTEXITCODE -ne 0 -or $trackedFiles.Count -eq 0) {
        throw 'Run this check from a Git checkout with the API source files.'
    }

    $sourceFiles = @('global.json', 'Directory.Build.props') + @($trackedFiles | Where-Object {
        $_ -notmatch '(^|/)(bin|obj)/' -and
        $_ -notmatch '^NutriFlow\.Api/(.*/)?(data|backups|label-photos)/' -and (
            $_ -match '\.(cs|csproj)$' -or
            $_ -match '/packages\.lock\.json$' -or
            $_ -in @('NutriFlow.Api/appsettings.json', 'NutriFlow.Api/appsettings.Development.json', 'NutriFlow.Api/Properties/launchSettings.json') -or
            $_ -match '^NutriFlow\.Api/wwwroot/.+\.(html|css|js|mjs|svg)$'
        )
    })

    foreach ($relativePath in $sourceFiles) {
        $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $relativePath))
        $sourceItem = Get-Item -LiteralPath $sourcePath

        if (-not $sourcePath.StartsWith("$repositoryRoot$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::Ordinal)) {
            throw 'A source file is outside the repository.'
        }

        $ancestor = $sourceItem

        while ($ancestor.FullName -ne $repositoryRoot) {
            if (($ancestor.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Linked source files are not supported: $relativePath"
            }

            $ancestor = Get-Item -LiteralPath ([System.IO.Path]::GetDirectoryName($ancestor.FullName))
        }

        $destinationPath = Join-Path $sourceRoot $relativePath
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
        [System.IO.File]::Copy($sourcePath, $destinationPath)
    }

    $apiRoot = Join-Path $sourceRoot 'NutriFlow.Api'
    $privatePaths = @(
        'data/nutriflow.db', 'data/nutriflow.db-wal', 'data/nutriflow.db-shm', 'data/nutriflow.db-journal',
        'data/label-photos/private.jpg', 'data/backups/archive/manifest.json', 'data/backups/archive/nutriflow.db',
        'data/validation/private.config', 'data/validation/private.json', 'data/PrivatePublishCanary.cs', 'backups/archive/manifest.json', 'label-photos/private.png',
        'wwwroot/data/private.json', 'wwwroot/backups/archive/manifest.json', 'wwwroot/label-photos/private.jpg',
        '.env', '.env.local', 'config/.env', 'config/.env.local',
        'appsettings.Local.json', 'config/appsettings.Local.json', 'wwwroot/config/appsettings.Local.json',
        'config/private.db', 'config/private.db-wal', 'config/private.db-shm', 'config/private.db-journal', 'config/private.db.bak',
        'config/private.sqlite', 'config/private.sqlite-wal', 'config/private.sqlite3', 'config/private.sqlite3-journal',
        'config/private.pfx', 'config/private.p12', 'config/private.pem', 'config/private.key',
        'wwwroot/config/.env', 'wwwroot/config/.env.local', 'wwwroot/config/private.db',
        'wwwroot/config/private.sqlite3', 'wwwroot/config/private.pfx', 'wwwroot/config/private.p12',
        'wwwroot/config/private.pem', 'wwwroot/config/private.key'
    )

    $marker = "nutriflow-private-canary-$([guid]::NewGuid().ToString('N'))"
    $privateHashes = @{}

    foreach ($relativePath in $privatePaths) {
        $privatePath = Join-Path $apiRoot $relativePath
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($privatePath)) | Out-Null
        $contents = if ($relativePath.EndsWith('.cs', [System.StringComparison]::Ordinal)) {
            "internal static class PrivatePublishCanary { public const string Value = `"$marker`"; }"
        }
        else {
            $marker
        }

        [System.IO.File]::WriteAllText($privatePath, $contents)
        $privateHashes[$relativePath] = (Get-FileHash -LiteralPath $privatePath -Algorithm SHA256).Hash
    }

    $projectPath = Join-Path $apiRoot 'NutriFlow.Api.csproj'
    $project = [xml](Get-Content -LiteralPath $projectPath -Raw)
    $targetFramework = [string]$project.Project.PropertyGroup.TargetFramework
    $buildRoot = Join-Path $apiRoot "bin/$Configuration/$targetFramework"

    Push-Location $sourceRoot

    try {
        dotnet restore $projectPath --locked-mode --warnaserror

        if ($LASTEXITCODE -ne 0) {
            throw 'The isolated restore failed.'
        }

        dotnet publish $projectPath --configuration $Configuration --no-restore --output $publishRoot /p:UseAppHost=false

        if ($LASTEXITCODE -ne 0) {
            throw 'The isolated publish failed.'
        }
    }
    finally {
        Pop-Location
    }

    $requiredFiles = @('NutriFlow.Api.dll', 'NutriFlow.Domain.dll', 'NutriFlow.Infrastructure.dll',
        'NutriFlow.Api.deps.json', 'NutriFlow.Api.runtimeconfig.json', 'appsettings.json', 'appsettings.Development.json')

    foreach ($outputRoot in @($buildRoot, $publishRoot)) {
        foreach ($relativePath in $requiredFiles) {
            if (-not (Test-Path -LiteralPath (Join-Path $outputRoot $relativePath) -PathType Leaf)) {
                throw "A required output file is missing: $relativePath"
            }
        }

        foreach ($relativePath in $privatePaths + @('Properties/launchSettings.json', 'data', 'backups', 'label-photos')) {
            if (Test-Path -LiteralPath (Join-Path $outputRoot $relativePath)) {
                throw "A private or tooling file was copied to an output: $relativePath"
            }
        }

        foreach ($outputFile in Get-ChildItem -LiteralPath $outputRoot -Recurse -File -Force) {
            $bytes = [System.IO.File]::ReadAllBytes($outputFile.FullName)
            $hasMarker = [System.Text.Encoding]::UTF8.GetString($bytes).Contains($marker, [System.StringComparison]::Ordinal) -or
                [System.Text.Encoding]::Unicode.GetString($bytes).Contains($marker, [System.StringComparison]::Ordinal)

            if (-not $hasMarker -and $bytes.Length -gt 1) {
                $hasMarker = [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1).Contains($marker, [System.StringComparison]::Ordinal)
            }

            if ($hasMarker) {
                throw 'Synthetic private data was found in an output file.'
            }
        }
    }

    foreach ($relativePath in @('appsettings.json', 'appsettings.Development.json', 'wwwroot/index.html', 'wwwroot/favicon.svg', 'wwwroot/css/app.css',
        'wwwroot/js/app.js', 'wwwroot/js/speech-capture.mjs')) {
        $publishedPath = Join-Path $publishRoot $relativePath
        $originalPath = Join-Path $apiRoot $relativePath

        if (-not (Test-Path -LiteralPath $publishedPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $originalPath -Algorithm SHA256).Hash) {
            throw "A public file is missing or changed: $relativePath"
        }
    }

    foreach ($relativePath in $privatePaths) {
        if ((Get-FileHash -LiteralPath (Join-Path $apiRoot $relativePath) -Algorithm SHA256).Hash -ne $privateHashes[$relativePath]) {
            throw 'A synthetic private input was changed during publication.'
        }
    }

    Write-Host "Publish check passed: $($privatePaths.Count) synthetic private files excluded; public settings, assemblies and assets preserved."
    $succeeded = $true
}
finally {
    if ($succeeded) {
        $resolvedRoot = [System.IO.Path]::GetFullPath($fixtureRoot)
        $fixtureItem = Get-Item -LiteralPath $resolvedRoot

        if ([System.IO.Path]::GetDirectoryName($resolvedRoot) -ne $temporaryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) -or
            [System.IO.Path]::GetFileName($resolvedRoot) -notmatch '^nutriflow-publish-[0-9a-f]{32}$' -or
            ($fixtureItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'The fixture cleanup path is invalid.'
        }

        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
    else {
        Write-Host "The isolated fixture was retained for inspection: $fixtureRoot"
    }
}
