#Requires -Version 7.2

param(
    [uri]$BaseUrl = 'http://localhost:5198/'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Speech

$client = [System.Net.Http.HttpClient]::new()
$client.BaseAddress = [uri]::new("$($BaseUrl.AbsoluteUri.TrimEnd('/'))/")
$client.Timeout = [TimeSpan]::FromSeconds(60)
$synthesizer = [System.Speech.Synthesis.SpeechSynthesizer]::new()
$buffer = [System.IO.MemoryStream]::new()

try {
    $capabilitiesJson = $client.GetStringAsync('api/capabilities').GetAwaiter().GetResult()
    $capabilities = $capabilitiesJson | ConvertFrom-Json

    if ($capabilities.aiProvider -ne 'Groq' -or -not $capabilities.supportsSpeechTranscription) {
        throw 'Start the current API with the Groq provider before running this check.'
    }

    $voice = $synthesizer.GetInstalledVoices() | Where-Object {
        $_.Enabled -and $_.VoiceInfo.Culture.Name -eq 'ru-RU'
    } | Select-Object -First 1

    if ($null -eq $voice) {
        throw 'A Russian Windows speech synthesis voice is required for the generated recording.'
    }

    $synthesizer.SelectVoice($voice.VoiceInfo.Name)
    $synthesizer.SetOutputToWaveStream($buffer)
    $synthesizer.Speak('Добавил примерно шестьсот граммов говядины. Готовое блюдо весит пятьсот граммов. Съел двести пятьдесят граммов.')
    $synthesizer.SetOutputToNull()

    $form = [System.Net.Http.MultipartFormDataContent]::new()
    $fileContent = [System.Net.Http.ByteArrayContent]::new($buffer.ToArray())
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('audio/wav')
    $form.Add($fileContent, 'audio', 'demo-speech.wav')

    try {
        $response = $client.PostAsync('api/audio/transcribe', $form).GetAwaiter().GetResult()

        try {
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

            if (-not $response.IsSuccessStatusCode) {
                throw "Transcription returned HTTP $([int]$response.StatusCode): $body"
            }

            $transcription = $body | ConvertFrom-Json

            if ([string]::IsNullOrWhiteSpace($transcription.text)) {
                throw 'No text was recognized from the generated speech.'
            }

            Write-Host "Transcript: $($transcription.text)"
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $form.Dispose()
    }

    $payload = @{ messages = @($transcription.text) } | ConvertTo-Json
    $requestContent = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, 'application/json')

    try {
        $response = $client.PostAsync('api/meal-drafts/parse', $requestContent).GetAwaiter().GetResult()

        try {
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

            if (-not $response.IsSuccessStatusCode) {
                throw "Meal parsing returned HTTP $([int]$response.StatusCode): $body"
            }

            $draft = $body | ConvertFrom-Json

            if ($draft.dishes.Count -ne 1 -or $draft.clarificationQuestions.Count -ne 0) {
                throw "The generated speech did not produce one complete dish draft: $body"
            }

            $dish = $draft.dishes[0]

            if ($dish.ingredients.Count -ne 1 -or $dish.portions.Count -ne 1 -or
                $dish.ingredients[0].productName -ne 'говядина' -or
                $dish.ingredients[0].weightInGrams -ne 600 -or
                $dish.ingredients[0].weightQuality -ne 'Estimated' -or
                $dish.finalWeightInGrams -ne 500 -or $dish.finalWeightQuality -ne 'Exact' -or
                $dish.portions[0].weightInGrams -ne 250 -or $dish.portions[0].weightQuality -ne 'Exact') {
                throw "The recognized meal values differ from the spoken example: $body"
            }

            Write-Host 'PASS: generated Russian speech -> text -> structured meal draft.'
            $draft | ConvertTo-Json -Depth 8
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $requestContent.Dispose()
    }
}
finally {
    $buffer.Dispose()
    $synthesizer.Dispose()
    $client.Dispose()
}
