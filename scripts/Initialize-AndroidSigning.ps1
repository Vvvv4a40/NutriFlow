param(
    [Parameter(Mandatory = $true)][string]$GitHubCliPath,
    [Parameter(Mandatory = $true)][string]$KeytoolPath
)

$ErrorActionPreference = 'Stop'
if (-not [System.OperatingSystem]::IsWindows()) {
    throw 'This helper protects the local password with Windows DPAPI.'
}

$taskRepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSigningDirectory = Join-Path $taskRepositoryRoot '.private/android-signing'
$taskKeyPath = Join-Path $taskSigningDirectory 'nutriflow-personal.keystore'
$taskPasswordPath = Join-Path $taskSigningDirectory 'password.dpapi'
if (-not (Test-Path -LiteralPath $GitHubCliPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $KeytoolPath -PathType Leaf)) {
    throw 'GitHub CLI and Java keytool must already be installed.'
}

function Assert-ExistingSigningMaterial {
    param([string[]]$SecretNames, [bool]$LocalKeyExists, [bool]$LocalPasswordExists)
    $taskSigningSecretNames = @('ANDROID_KEYSTORE_BASE64', 'ANDROID_KEYSTORE_PASSWORD', 'ANDROID_KEY_ALIAS')
    $taskExistingSigningSecrets = @($SecretNames | Where-Object { $_ -in $taskSigningSecretNames })
    if ($taskExistingSigningSecrets.Count -gt 0 -and (-not $LocalKeyExists -or -not $LocalPasswordExists)) {
        throw 'Android signing is already configured in GitHub, but the local key or protected password is missing. Restore the original local signing material; a new key will not be generated and GitHub secrets will not be replaced.'
    }
}

Push-Location $taskRepositoryRoot
try {
    foreach ($taskIgnoredPath in @('.private/android-signing/nutriflow-personal.keystore', '.private/android-signing/password.dpapi')) {
        & git check-ignore --quiet -- $taskIgnoredPath
        if ($LASTEXITCODE -ne 0) { throw 'The signing key and protected password must be ignored by Git.' }
    }
    $taskRepositoryName = & $GitHubCliPath repo view --json nameWithOwner --jq '.nameWithOwner'
    if ($LASTEXITCODE -ne 0 -or $taskRepositoryName -ne 'Vvvv4a40/NutriFlow') {
        throw 'Cannot verify the intended GitHub repository.'
    }
    $taskRemoteSecretNames = @(& $GitHubCliPath secret list --repo 'Vvvv4a40/NutriFlow' --json name --jq '.[].name')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify existing Android signing secret names in GitHub.' }
    Assert-ExistingSigningMaterial -SecretNames $taskRemoteSecretNames `
        -LocalKeyExists (Test-Path -LiteralPath $taskKeyPath -PathType Leaf) `
        -LocalPasswordExists (Test-Path -LiteralPath $taskPasswordPath -PathType Leaf)
}
finally { Pop-Location }

[System.IO.Directory]::CreateDirectory($taskSigningDirectory) | Out-Null
if (Test-Path -LiteralPath $taskPasswordPath -PathType Leaf) {
    $taskProtectedBytes = [System.IO.File]::ReadAllBytes($taskPasswordPath)
    $taskPasswordBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
        $taskProtectedBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    $taskSigningPassword = [System.Text.Encoding]::UTF8.GetString($taskPasswordBytes)
}
else {
    if (Test-Path -LiteralPath $taskKeyPath) { throw 'Existing signing key has no protected password. It will not be replaced.' }
    $taskSigningPassword = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $taskProtectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
        [System.Text.Encoding]::UTF8.GetBytes($taskSigningPassword), $null,
        [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    [System.IO.File]::WriteAllBytes($taskPasswordPath, $taskProtectedBytes)
}

function Invoke-PrivateKeytool {
    param([string[]]$Arguments)
    $taskStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $taskStartInfo.FileName = $KeytoolPath
    $taskStartInfo.UseShellExecute = $false
    $taskStartInfo.CreateNoWindow = $true
    $taskStartInfo.RedirectStandardOutput = $true
    $taskStartInfo.RedirectStandardError = $true
    $taskStartInfo.Environment['NUTRIFLOW_ANDROID_SIGNING_PASSWORD'] = $taskSigningPassword
    foreach ($taskArgument in $Arguments) { $taskStartInfo.ArgumentList.Add($taskArgument) }
    $taskProcess = [System.Diagnostics.Process]::Start($taskStartInfo)
    $taskOutput = $taskProcess.StandardOutput.ReadToEndAsync()
    $taskErrorOutput = $taskProcess.StandardError.ReadToEndAsync()
    $taskProcess.WaitForExit()
    $taskOutput.GetAwaiter().GetResult() | Out-Null
    $taskErrorOutput.GetAwaiter().GetResult() | Out-Null
    $taskExitCode = $taskProcess.ExitCode
    $taskProcess.Dispose()
    if ($taskExitCode -ne 0) { throw "keytool failed with exit code $taskExitCode. Signing material was not replaced." }
}

if (-not (Test-Path -LiteralPath $taskKeyPath)) {
    Invoke-PrivateKeytool -Arguments @(
        '-genkeypair', '-storetype', 'JKS', '-keystore', $taskKeyPath,
        '-alias', 'nutriflow-personal', '-keyalg', 'RSA', '-keysize', '3072', '-validity', '10000',
        '-dname', 'CN=NutriFlow Personal,OU=Personal,O=NutriFlow,L=Local,ST=Local,C=RU',
        '-storepass:env', 'NUTRIFLOW_ANDROID_SIGNING_PASSWORD',
        '-keypass:env', 'NUTRIFLOW_ANDROID_SIGNING_PASSWORD', '-noprompt'
    )
}
Invoke-PrivateKeytool -Arguments @(
    '-list', '-keystore', $taskKeyPath, '-alias', 'nutriflow-personal',
    '-storepass:env', 'NUTRIFLOW_ANDROID_SIGNING_PASSWORD'
)

function Set-PrivateGitHubSecret {
    param([string]$Name, [string]$Value)
    $taskStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $taskStartInfo.FileName = $GitHubCliPath
    $taskStartInfo.UseShellExecute = $false
    $taskStartInfo.CreateNoWindow = $true
    $taskStartInfo.RedirectStandardInput = $true
    $taskStartInfo.RedirectStandardOutput = $true
    $taskStartInfo.RedirectStandardError = $true
    foreach ($taskArgument in @('secret', 'set', $Name, '--repo', 'Vvvv4a40/NutriFlow')) {
        $taskStartInfo.ArgumentList.Add($taskArgument)
    }
    $taskProcess = [System.Diagnostics.Process]::Start($taskStartInfo)
    $taskOutput = $taskProcess.StandardOutput.ReadToEndAsync()
    $taskErrorOutput = $taskProcess.StandardError.ReadToEndAsync()
    $taskProcess.StandardInput.Write($Value)
    $taskProcess.StandardInput.Close()
    $taskProcess.WaitForExit()
    $taskOutput.GetAwaiter().GetResult() | Out-Null
    $taskErrorOutput.GetAwaiter().GetResult() | Out-Null
    $taskExitCode = $taskProcess.ExitCode
    $taskProcess.Dispose()
    if ($taskExitCode -ne 0) { throw "GitHub did not accept secret $Name; exit code $taskExitCode." }
    Write-Output "Configured $Name."
}

Set-PrivateGitHubSecret -Name 'ANDROID_KEYSTORE_BASE64' -Value ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($taskKeyPath)))
Set-PrivateGitHubSecret -Name 'ANDROID_KEYSTORE_PASSWORD' -Value $taskSigningPassword
Set-PrivateGitHubSecret -Name 'ANDROID_KEY_ALIAS' -Value 'nutriflow-personal'
Write-Output 'Permanent personal Android signing configured; credentials were not printed.'
