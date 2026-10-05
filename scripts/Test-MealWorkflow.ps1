#Requires -Version 7.2

param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$apiAssembly = Join-Path $repositoryRoot "NutriFlow.Api/bin/$Configuration/net10.0/NutriFlow.Api.dll"
$dotnetPath = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$trialRoot = Join-Path ([System.IO.Path]::GetTempPath()) "nutriflow-workflow-$([guid]::NewGuid().ToString('N'))"
$mealDate = '2026-10-06'
$fractionalMealDate = '2026-10-07'
$server = $null

if (-not (Test-Path -LiteralPath $apiAssembly -PathType Leaf)) {
    throw "Build the solution first: dotnet build NutriFlow.sln --configuration $Configuration"
}
if (Test-Path -LiteralPath $trialRoot) {
    throw 'The workflow fixture directory already exists.'
}
[System.IO.Directory]::CreateDirectory($trialRoot) | Out-Null

function Assert-Equal {
    param($Expected, $Actual, [string]$Field)

    if ($Expected -ne $Actual) {
        throw "Unexpected ${Field}: expected '$Expected', received '$Actual'."
    }
}

function Assert-Nutrition {
    param([System.Text.Json.JsonElement]$Nutrition, [decimal[]]$Values)

    $fields = @('calories', 'proteinGrams', 'fatGrams', 'carbohydratesGrams')
    for ($index = 0; $index -lt $fields.Count; $index++) {
        $value = $Nutrition.GetProperty($fields[$index])
        Assert-Equal ([System.Text.Json.JsonValueKind]::Number) $value.ValueKind $fields[$index]
        Assert-Equal $Values[$index] $value.GetDecimal() $fields[$index]
    }
}

function Stop-Api {
    param($Server)

    if ($null -eq $Server) { return }
    try {
        $Server.Client.Dispose()
        if (-not $Server.Process.HasExited) {
            $Server.Process.Kill($true)
            if (-not $Server.Process.WaitForExit(10000)) {
                throw "The private API process did not stop: $($Server.Process.Id)."
            }
        }
    }
    finally {
        $Server.Process.Dispose()
    }
}

function Start-Api {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($dotnetPath)
    $startInfo.WorkingDirectory = $trialRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $startInfo.Environment.Clear()
    foreach ($name in @('PATH', 'SystemRoot', 'WINDIR', 'TEMP', 'TMP', 'HOME', 'USERPROFILE', 'DOTNET_ROOT', 'DOTNET_ROOT_X64')) {
        $value = [System.Environment]::GetEnvironmentVariable($name)
        if ($null -ne $value) { $startInfo.Environment[$name] = $value }
    }
    $startInfo.Environment['DOTNET_ENVIRONMENT'] = 'Production'
    $startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $startInfo.Environment['ASPNETCORE_PREVENTHOSTINGSTARTUP'] = 'true'
    $startInfo.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = 'true'
    $arguments = @(
        $apiAssembly,
        '--urls', 'http://127.0.0.1:0',
        '--Ai:Provider', 'Fake',
        '--Demo:SeedData', 'true',
        '--Database:ApplyMigrationsOnStartup', 'true',
        '--Database:Path', (Join-Path $trialRoot 'nutriflow.db'),
        '--Storage:LabelPhotosPath', (Join-Path $trialRoot 'label-photos'),
        '--OpenFoodFacts:BaseUrl', 'http://127.0.0.1:1/',
        '--Logging:LogLevel:Default', 'Warning',
        '--Logging:EventLog:LogLevel:Default', 'None',
        '--Logging:LogLevel:Microsoft.Hosting.Lifetime', 'Information'
    )
    foreach ($argument in $arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $client = $null
    $started = $false
    try {
        $started = $process.Start()
        if (-not $started) { throw 'The private API process did not start.' }
        $errors = $process.StandardError.ReadToEndAsync()
        $line = $process.StandardOutput.ReadLineAsync()
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        $baseUrl = $null
        while ($null -eq $baseUrl) {
            if ($timer.Elapsed.TotalSeconds -gt 45) { throw 'The private API startup timed out.' }
            if ($process.HasExited) {
                throw "The private API exited with code $($process.ExitCode): $($errors.GetAwaiter().GetResult())"
            }
            if ($line.IsCompleted) {
                $text = $line.GetAwaiter().GetResult()
                if ($null -eq $text) { throw 'The private API output ended before startup.' }
                if ($text -match 'Now listening on: (http://127\.0\.0\.1:[0-9]+)\s*$') {
                    $baseUrl = [uri]::new("$($Matches[1])/")
                }
                else {
                    $line = $process.StandardOutput.ReadLineAsync()
                }
            }
            else {
                [System.Threading.Tasks.Task]::Delay(100).GetAwaiter().GetResult()
            }
        }
        $output = $process.StandardOutput.ReadToEndAsync()
        $client = [System.Net.Http.HttpClient]::new()
        $client.BaseAddress = $baseUrl
        $client.Timeout = [TimeSpan]::FromSeconds(10)
        foreach ($path in @('health/live', 'health/ready')) {
            $response = $client.GetAsync($path).GetAwaiter().GetResult()
            try { Assert-Equal 200 ([int]$response.StatusCode) $path }
            finally { $response.Dispose() }
        }
        return [pscustomobject]@{ Process = $process; Client = $client; Output = $output; Errors = $errors }
    }
    catch {
        if ($null -ne $client) { $client.Dispose() }
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $process.WaitForExit(10000) | Out-Null
        }
        $process.Dispose()
        throw
    }
}

function Invoke-Api {
    param([string]$Method, [string]$Path, $Body = $null, [int]$Status = 200, [string]$IdempotencyKey = '')

    if ($server.Process.HasExited) { throw 'The private API stopped during the workflow.' }
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Path)
    $response = $null
    try {
        if ($null -ne $Body) {
            $json = ConvertTo-Json -InputObject $Body -Depth 6 -Compress
            $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
        }
        if ($IdempotencyKey) { $request.Headers.Add('Idempotency-Key', $IdempotencyKey) }
        $response = $server.Client.SendAsync($request).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne $Status) {
            throw "$Method $Path returned HTTP $([int]$response.StatusCode), expected ${Status}: $text"
        }
        Assert-Equal 'application/json' $response.Content.Headers.ContentType.MediaType "$Path content type"
        Assert-Equal $true $response.Headers.CacheControl.Private "$Path private cache"
        Assert-Equal $true $response.Headers.CacheControl.NoStore "$Path no-store"
        $document = [System.Text.Json.JsonDocument]::Parse($text)
        try { return $document.RootElement.Clone() }
        finally { $document.Dispose() }
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose()
    }
}

function Confirm-Session {
    param([System.Text.Json.JsonElement]$Session, [string]$Outcome = 'Confirmed')

    $id = $Session.GetProperty('id').GetString()
    $result = Invoke-Api POST "api/meal-sessions/$id/confirm" @{ previewToken = $Session.GetProperty('previewToken').GetString() }
    Assert-Equal $Outcome $result.GetProperty('outcome').GetString() 'confirmation outcome'
    return $result
}

try {
    Write-Host "Private synthetic storage: $trialRoot"
    $server = Start-Api
    $capabilities = Invoke-Api GET 'api/capabilities'
    Assert-Equal 'Fake' $capabilities.GetProperty('aiProvider').GetString() 'provider'
    foreach ($field in @('supportsFreeText', 'supportsLabelPhotos', 'supportsSpeechTranscription')) {
        Assert-Equal $false $capabilities.GetProperty($field).GetBoolean() $field
    }
    $day = Invoke-Api GET "api/daily-progress/$mealDate"
    Assert-Equal 0 $day.GetProperty('entries').GetArrayLength() 'initial diary'
    $dishes = Invoke-Api GET 'api/saved-dishes'
    Assert-Equal 0 $dishes.GetArrayLength() 'initial saved dishes'
    $dishBody = @{
        purpose = 'CreateDish'
        mealDate = $mealDate
        messages = @('Добавил 200 г демо-продукта A.', 'Потом добавил 100 г демо-продукта B.', 'Готовое блюдо весит 250 г.')
    }
    $creationKey = [guid]::NewGuid().ToString()
    $dishSession = Invoke-Api POST 'api/meal-sessions' $dishBody 201 $creationKey
    Assert-Equal 'CreateDish' $dishSession.GetProperty('purpose').GetString() 'dish purpose'
    Assert-Equal $true $dishSession.GetProperty('canConfirm').GetBoolean() 'dish canConfirm'
    Assert-Equal 0 $dishSession.GetProperty('clarificationQuestions').GetArrayLength() 'dish questions'
    Assert-Equal 1 $dishSession.GetProperty('dishes').GetArrayLength() 'dish count'
    $preview = $dishSession.GetProperty('dishes')[0]
    Assert-Equal 0 $preview.GetProperty('portions').GetArrayLength() 'dish portions'
    Assert-Nutrition $preview.GetProperty('totalNutrition') @(400, 25, 20, 30)
    $replay = Invoke-Api POST 'api/meal-sessions' $dishBody 200 $creationKey
    Assert-Equal $dishSession.GetProperty('id').GetString() $replay.GetProperty('id').GetString() 'creation replay id'
    Assert-Equal $dishSession.GetProperty('previewToken').GetString() $replay.GetProperty('previewToken').GetString() 'creation replay token'
    $confirmed = Confirm-Session $dishSession
    Assert-Equal 0 $confirmed.GetProperty('entries').GetArrayLength() 'dish diary entries'
    $saved = $confirmed.GetProperty('savedDish')
    $savedId = $saved.GetProperty('id').GetString()
    Assert-Equal 'Демо-блюдо' $saved.GetProperty('name').GetString() 'saved name'
    Assert-Equal ([decimal]250) $saved.GetProperty('finalWeightInGrams').GetDecimal() 'saved weight'
    Assert-Equal 'Unknown' $saved.GetProperty('nutritionQuality').GetString() 'saved quality'
    Assert-Nutrition $saved.GetProperty('nutritionPer100Grams') @(160, 10, 8, 12)
    $repeatedDish = Confirm-Session $dishSession 'AlreadyConfirmed'
    Assert-Equal $savedId $repeatedDish.GetProperty('savedDish').GetProperty('id').GetString() 'repeated saved id'
    $detail = Invoke-Api GET "api/saved-dishes/$savedId"
    $ingredients = $detail.GetProperty('dish').GetProperty('ingredients')
    Assert-Equal 2 $ingredients.GetArrayLength() 'saved composition'
    foreach ($ingredient in $ingredients.EnumerateArray()) {
        $product = $ingredient.GetProperty('resolvedProduct')
        Assert-Equal 'NutriFlowCatalog' $product.GetProperty('sourceKind').GetString() 'ingredient source'
        Assert-Equal 'NutriFlow demo dataset' $product.GetProperty('sourceName').GetString() 'ingredient source name'
    }
    $day = Invoke-Api GET "api/daily-progress/$mealDate"
    Assert-Equal 0 $day.GetProperty('entries').GetArrayLength() 'diary after dish creation'
    Write-Host 'PASS: CreateDish preserves composition and sources without diary entries.'

    $mealBody = @{ purpose = 'Diary'; mealDate = $mealDate; messages = @('Съел 100 г Демо-блюда.') }
    $mealKey = [guid]::NewGuid().ToString()
    $meal = Invoke-Api POST 'api/meal-sessions' $mealBody 201 $mealKey
    Assert-Equal $true $meal.GetProperty('canConfirm').GetBoolean() 'meal canConfirm'
    $product = $meal.GetProperty('dishes')[0].GetProperty('ingredients')[0].GetProperty('resolvedProduct')
    Assert-Equal 'SavedDish' $product.GetProperty('sourceKind').GetString() 'reused source'
    Assert-Equal "saved-dish:$([guid]::Parse($savedId).ToString('N'))" $product.GetProperty('sourceReference').GetString() 'reused reference'
    $confirmedMeal = Confirm-Session $meal
    Assert-Equal 1 $confirmedMeal.GetProperty('entries').GetArrayLength() 'confirmed meal entries'
    $entry = $confirmedMeal.GetProperty('entries')[0]
    Assert-Equal ([decimal]100) $entry.GetProperty('weightInGrams').GetDecimal() 'portion weight'
    Assert-Equal 'Unknown' $entry.GetProperty('quality').GetString() 'portion quality'
    Assert-Nutrition $entry.GetProperty('nutrition') @(160, 10, 8, 12)
    $repeatedMeal = Confirm-Session $meal 'AlreadyConfirmed'
    Assert-Equal $entry.GetProperty('id').GetInt32() $repeatedMeal.GetProperty('entries')[0].GetProperty('id').GetInt32() 'repeated entry id'
    $day = Invoke-Api GET "api/daily-progress/$mealDate"
    Assert-Equal 1 $day.GetProperty('entries').GetArrayLength() 'diary count'
    Assert-Nutrition $day.GetProperty('consumed') @(160, 10, 8, 12)
    $unchangedDish = Invoke-Api GET "api/saved-dishes/$savedId"
    Assert-Equal $detail.GetRawText() $unchangedDish.GetRawText() 'saved composition after meal'
    Write-Host 'PASS: Diary reuses the saved dish; retries do not duplicate data.'

    $fractionalBody = @{
        purpose = 'Diary'
        mealDate = $fractionalMealDate
        messages = @(
            'Добавил 200 г демо-продукта A.',
            'Потом добавил 100 г демо-продукта B.',
            'Готовое блюдо весит 250 г.',
            'Съел 125 г, потом ещё две порции по 62,5 г.'
        )
    }
    $fractionalMeal = Invoke-Api POST 'api/meal-sessions' $fractionalBody 201 ([guid]::NewGuid().ToString())
    $fractionalConfirmation = Confirm-Session $fractionalMeal
    $fractionalEntries = $fractionalConfirmation.GetProperty('entries')
    Assert-Equal 3 $fractionalEntries.GetArrayLength() 'fractional meal entries'
    Assert-Equal ([decimal]125) $fractionalEntries[0].GetProperty('weightInGrams').GetDecimal() 'first portion weight'
    Assert-Nutrition $fractionalEntries[0].GetProperty('nutrition') @(200, 12.5, 10, 15)
    foreach ($index in @(1, 2)) {
        $weight = $fractionalEntries[$index].GetProperty('weightInGrams')
        Assert-Equal ([System.Text.Json.JsonValueKind]::Number) $weight.ValueKind 'fractional weight JSON type'
        Assert-Equal ([decimal]62.5) $weight.GetDecimal() 'fractional portion weight'
        Assert-Nutrition $fractionalEntries[$index].GetProperty('nutrition') @(100, 6.25, 5, 7.5)
    }
    $fractionalDay = Invoke-Api GET "api/daily-progress/$fractionalMealDate"
    Assert-Equal 3 $fractionalDay.GetProperty('entries').GetArrayLength() 'fractional diary count'
    Assert-Nutrition $fractionalDay.GetProperty('consumed') @(400, 25, 20, 30)
    Write-Host 'PASS: Fractional weights and nutrition remain exact JSON numbers.'

    Stop-Api $server
    $server = $null
    $server = Start-Api
    $repeatedDish = Confirm-Session $dishSession 'AlreadyConfirmed'
    Assert-Equal $savedId $repeatedDish.GetProperty('savedDish').GetProperty('id').GetString() 'saved id after restart'
    $repeatedMeal = Confirm-Session $meal 'AlreadyConfirmed'
    Assert-Equal $entry.GetProperty('id').GetInt32() $repeatedMeal.GetProperty('entries')[0].GetProperty('id').GetInt32() 'entry id after restart'
    $replay = Invoke-Api POST 'api/meal-sessions' $mealBody 200 $mealKey
    Assert-Equal $meal.GetProperty('id').GetString() $replay.GetProperty('id').GetString() 'meal creation replay after restart'
    $dishes = Invoke-Api GET 'api/saved-dishes'
    Assert-Equal 1 $dishes.GetArrayLength() 'saved dishes after restart'
    $day = Invoke-Api GET "api/daily-progress/$mealDate"
    Assert-Equal 1 $day.GetProperty('entries').GetArrayLength() 'diary after restart'
    Assert-Nutrition $day.GetProperty('consumed') @(160, 10, 8, 12)
    $unchangedDish = Invoke-Api GET "api/saved-dishes/$savedId"
    Assert-Equal $detail.GetRawText() $unchangedDish.GetRawText() 'saved composition after restart'
    $repeatedFractionalMeal = Confirm-Session $fractionalMeal 'AlreadyConfirmed'
    Assert-Equal $fractionalEntries.GetRawText() $repeatedFractionalMeal.GetProperty('entries').GetRawText() 'fractional entries after restart'
    $fractionalDay = Invoke-Api GET "api/daily-progress/$fractionalMealDate"
    Assert-Equal 3 $fractionalDay.GetProperty('entries').GetArrayLength() 'fractional diary after restart'
    Assert-Nutrition $fractionalDay.GetProperty('consumed') @(400, 25, 20, 30)
    Write-Host 'PASS: Server restart preserves the dish, diary and retry protection.'
}
finally {
    Stop-Api $server
    Write-Host "Synthetic storage retained for inspection: $trialRoot"
}
