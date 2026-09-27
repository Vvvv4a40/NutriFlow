#Requires -Version 7.2

param(
    [uri]$BaseUrl = 'http://localhost:5198/'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

function New-LabelImage {
    param([bool]$KilojoulesOnly)

    $bitmap = [System.Drawing.Bitmap]::new(1000, 620)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $titleFont = [System.Drawing.Font]::new('Arial', 30, [System.Drawing.FontStyle]::Bold)
    $textFont = [System.Drawing.Font]::new('Arial', 26)
    $buffer = [System.IO.MemoryStream]::new()

    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.DrawString('NutriFlow Demo Yogurt', $titleFont, [System.Drawing.Brushes]::Black, 35, 30)

        $energy = if ($KilojoulesOnly) { 'Energy: 316 kJ' } else { 'Energy: 316 kJ / 75.5 kcal' }
        $lines = @(
            'NUTRITION FACTS - PER 100 g',
            $energy,
            'Protein: 4.2 g',
            'Fat: 3.1 g',
            'Carbohydrates: 7.6 g'
        )

        for ($index = 0; $index -lt $lines.Count; $index++) {
            $graphics.DrawString($lines[$index], $textFont, [System.Drawing.Brushes]::Black, 35, (120 + $index * 75))
        }

        $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$buffer.ToArray()
    }
    finally {
        $buffer.Dispose()
        $textFont.Dispose()
        $titleFont.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Field)

    if ($Expected -ne $Actual) {
        throw "Unexpected ${Field}: expected '$Expected', received '$Actual'."
    }
}

function Test-Label {
    param(
        [System.Net.Http.HttpClient]$Client,
        [bool]$KilojoulesOnly
    )

    [byte[]]$image = New-LabelImage -KilojoulesOnly $KilojoulesOnly
    $form = [System.Net.Http.MultipartFormDataContent]::new()
    $photoContent = [System.Net.Http.ByteArrayContent]::new($image)
    $photoContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('image/png')
    $form.Add($photoContent, 'photo', 'demo-label.png')
    $response = $null

    try {
        $response = $Client.PostAsync('api/labels/analyze', $form).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

        if (-not $response.IsSuccessStatusCode) {
            throw "Label analysis returned HTTP $([int]$response.StatusCode): $body"
        }

        $draft = $body | ConvertFrom-Json
        $draft | ConvertTo-Json -Depth 3
        Assert-Equal 'Per100Grams' $draft.basis 'basis'
        Assert-Equal 4.2 $draft.proteinGrams 'proteinGrams'
        Assert-Equal 3.1 $draft.fatGrams 'fatGrams'
        Assert-Equal 7.6 $draft.carbohydratesGrams 'carbohydratesGrams'
        Assert-Equal 'NutriFlow Demo Yogurt' $draft.productName 'productName'

        if ($KilojoulesOnly) {
            Assert-Equal $null $draft.calories 'calories'
            Assert-Equal $false $draft.canCreateProduct 'canCreateProduct'

            if ($draft.clarificationQuestions.Count -eq 0) {
                throw 'A kJ-only label must request clarification for missing kcal.'
            }

            $calorieQuestions = @($draft.clarificationQuestions | Where-Object {
                $_ -match 'kcal|kilocalor|ккал|килокалор'
            })

            if ($calorieQuestions.Count -eq 0) {
                throw 'The clarification must concern the missing kilocalorie value.'
            }
        }
        else {
            Assert-Equal 75.5 $draft.calories 'calories'
            Assert-Equal $true $draft.canCreateProduct 'canCreateProduct'
            Assert-Equal 0 $draft.clarificationQuestions.Count 'clarificationQuestions.Count'
        }

        if ($draft.photoReference -notmatch '^label-photo:([a-f0-9]{32}\.png)$') {
            throw 'The response does not reference a saved PNG source.'
        }

        $photoResponse = $Client.GetAsync("api/label-photos/$($Matches[1])").GetAwaiter().GetResult()

        try {
            $photoResponse.EnsureSuccessStatusCode() | Out-Null
            Assert-Equal 'image/png' $photoResponse.Content.Headers.ContentType.MediaType 'photo media type'
            [byte[]]$storedImage = $photoResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            $originalHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($image))
            $storedHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($storedImage))
            Assert-Equal $originalHash $storedHash 'saved photo hash'
        }
        finally {
            $photoResponse.Dispose()
        }

        $caseName = if ($KilojoulesOnly) { 'kJ-only label' } else { 'complete label' }
        Write-Host "PASS: $caseName, nutrition values and saved source verified."
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $form.Dispose()
    }
}

$client = [System.Net.Http.HttpClient]::new()
$client.BaseAddress = [uri]::new("$($BaseUrl.AbsoluteUri.TrimEnd('/'))/")
$client.Timeout = [TimeSpan]::FromSeconds(60)

try {
    $capabilitiesJson = $client.GetStringAsync('api/capabilities').GetAwaiter().GetResult()
    $capabilities = $capabilitiesJson | ConvertFrom-Json
    Assert-Equal 'Groq' $capabilities.aiProvider 'aiProvider'
    Assert-Equal $true $capabilities.supportsLabelPhotos 'supportsLabelPhotos'

    Test-Label -Client $client -KilojoulesOnly $false
    Test-Label -Client $client -KilojoulesOnly $true
}
finally {
    $client.Dispose()
}
