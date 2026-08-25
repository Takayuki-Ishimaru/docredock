param(
    [string[]] $RuntimeIds = @(
        "win-x64", "win-arm64",
        "osx-x64", "osx-arm64",
        "linux-x64", "linux-arm64"
    )
)

$ErrorActionPreference = "Stop"
$RepositoryRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepositoryRoot "src/DocRedock.Cli/DocRedock.Cli.csproj"
$OutputRoot = Join-Path $RepositoryRoot "artifacts/cli"
$SupportedRuntimeIds = @(
    "win-x64", "win-arm64",
    "osx-x64", "osx-arm64",
    "linux-x64", "linux-arm64"
)

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
foreach ($RuntimeId in $RuntimeIds) {
    if ($RuntimeId -notin $SupportedRuntimeIds) {
        throw "Unsupported runtime identifier: $RuntimeId"
    }

    Write-Host "Publishing DocRedock CLI for $RuntimeId"
    $Output = Join-Path $OutputRoot $RuntimeId
    if (Test-Path $Output) {
        Remove-Item -Recurse -Force -LiteralPath $Output
    }
    & dotnet publish $Project `
        --configuration Release `
        --runtime $RuntimeId `
        --self-contained true `
        --output $Output `
        -p:PublishSingleFile=true `
        -p:UseAppHost=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        "-p:NuGetLockFilePath=obj/publish-locks/$RuntimeId/packages.lock.json" `
        -p:RestoreForceEvaluate=true `
        -p:RestoreLockedMode=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $RuntimeId with exit code $LASTEXITCODE"
    }
}

Write-Host "Published CLI builds: $OutputRoot"
