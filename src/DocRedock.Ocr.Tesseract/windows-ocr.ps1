# Invoked only by WindowsOcrEngine. It uses the inbox Windows.Media.Ocr WinRT
# API and writes a JSON array of line text and image-pixel bounding boxes.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$ImagePath,
    [Parameter(ValueFromRemainingArguments = $true, Position = 1)]
    [string[]]$Languages
)

$ErrorActionPreference = 'Stop'
$unavailableExitCode = 10
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

function Exit-Unavailable([string]$Message) {
    [Console]::Error.WriteLine("DRMD_OCR_UNAVAILABLE: $Message")
    exit $unavailableExitCode
}

function Get-AsyncResult($Operation, [Type]$ResultType) {
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object {
            $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and
            $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -like 'IAsyncOperation*'
        } |
        Select-Object -First 1
    if ($null -eq $method) { Exit-Unavailable 'System.Runtime.WindowsRuntime does not expose IAsyncOperation support.' }
    $task = $method.MakeGenericMethod([Type[]]@($ResultType)).Invoke($null, @($Operation))
    $null = $task.Wait()
    return $task.Result
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        Exit-Unavailable 'Windows.Media.Ocr is available only on Windows.'
    }
    if (-not (Test-Path -LiteralPath $ImagePath -PathType Leaf)) {
        throw "OCR image was not found at '$ImagePath'."
    }

    [void][System.Reflection.Assembly]::LoadWithPartialName('System.Runtime.WindowsRuntime')
    $storageFileType = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
    $fileAccessModeType = [Windows.Storage.FileAccessMode, Windows.Storage, ContentType = WindowsRuntime]
    $randomAccessStreamType = [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage, ContentType = WindowsRuntime]
    $bitmapDecoderType = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
    $softwareBitmapType = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
    $ocrEngineType = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
    $ocrResultType = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType = WindowsRuntime]

    $requestedTags = foreach ($language in $Languages) {
        switch ($language.ToLowerInvariant()) {
            'jpn' { 'ja-JP'; break }
            'ja' { 'ja-JP'; break }
            'ja-jp' { 'ja-JP'; break }
            'eng' { 'en-US'; break }
            'en' { 'en-US'; break }
            'en-us' { 'en-US'; break }
            default { $language }
        }
    }
    if ($requestedTags.Count -eq 0) { $requestedTags = @('ja-JP', 'en-US') }

    $engine = $null
    $availableLanguages = @($ocrEngineType::AvailableRecognizerLanguages)
    foreach ($requestedTag in $requestedTags) {
        $requestedBaseTag = $requestedTag.Split('-')[0]
        $selectedLanguage = @($availableLanguages | Where-Object {
            $_.LanguageTag -ieq $requestedTag -or
            $_.LanguageTag -ieq $requestedBaseTag -or
            $_.LanguageTag.StartsWith("$requestedBaseTag-", [StringComparison]::OrdinalIgnoreCase)
        }) | Select-Object -First 1
        if ($null -ne $selectedLanguage) {
            $engine = $ocrEngineType::TryCreateFromLanguage($selectedLanguage)
            if ($null -ne $engine) { break }
        }
    }
    if ($null -eq $engine) { $engine = $ocrEngineType::TryCreateFromUserProfileLanguages() }
    if ($null -eq $engine) {
        Exit-Unavailable 'No Windows OCR language pack is installed for the requested or user-profile languages.'
    }

    $file = Get-AsyncResult ($storageFileType::GetFileFromPathAsync($ImagePath)) $storageFileType
    $stream = Get-AsyncResult ($file.OpenAsync($fileAccessModeType::Read)) $randomAccessStreamType
    try {
        $decoder = Get-AsyncResult ($bitmapDecoderType::CreateAsync($stream)) $bitmapDecoderType
        $bitmap = Get-AsyncResult ($decoder.GetSoftwareBitmapAsync()) $softwareBitmapType
        try {
            $result = Get-AsyncResult ($engine.RecognizeAsync($bitmap)) $ocrResultType
            $regions = [System.Collections.Generic.List[object]]::new()
            foreach ($line in $result.Lines) {
                $words = @($line.Words)
                if ($words.Count -eq 0) { continue }
                $left = [double]::PositiveInfinity
                $top = [double]::PositiveInfinity
                $right = [double]::NegativeInfinity
                $bottom = [double]::NegativeInfinity
                foreach ($word in $words) {
                    $box = $word.BoundingRect
                    $left = [Math]::Min($left, [double]$box.X)
                    $top = [Math]::Min($top, [double]$box.Y)
                    $right = [Math]::Max($right, [double]$box.X + [double]$box.Width)
                    $bottom = [Math]::Max($bottom, [double]$box.Y + [double]$box.Height)
                }
                $regions.Add([ordered]@{
                    text = [string]$line.Text
                    x = $left
                    y = $top
                    width = $right - $left
                    height = $bottom - $top
                })
            }
            [Console]::Out.Write((ConvertTo-Json -InputObject ([object[]]$regions.ToArray()) -Compress -Depth 3))
        }
        finally { if ($null -ne $bitmap) { $bitmap.Dispose() } }
    }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
}
catch {
    [Console]::Error.WriteLine("DRMD_OCR_FAILED: $($_.Exception.Message)")
    exit 20
}
