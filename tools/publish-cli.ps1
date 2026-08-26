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
    $RuntimeLockPath = "obj/runtime-locks/packages.$RuntimeId.lock.json"
    & dotnet restore $Project --runtime $RuntimeId --force-evaluate --use-lock-file --lock-file-path $RuntimeLockPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet runtime lock generation failed for $RuntimeId with exit code $LASTEXITCODE"
    }
    & dotnet restore $Project --runtime $RuntimeId --locked-mode --use-lock-file --lock-file-path $RuntimeLockPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet locked restore failed for $RuntimeId with exit code $LASTEXITCODE"
    }
    & dotnet publish $Project `
        --configuration Release `
        --runtime $RuntimeId `
        --self-contained true `
        --no-restore `
        --output $Output `
        -p:PublishSingleFile=true `
        -p:UseAppHost=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=None
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $RuntimeId with exit code $LASTEXITCODE"
    }

    $RuntimeLockOutput = Join-Path $Output "runtime-locks"
    New-Item -ItemType Directory -Force -Path $RuntimeLockOutput | Out-Null
    Get-ChildItem -Path (Join-Path $RepositoryRoot "src") -Filter "packages.$RuntimeId.lock.json" -Recurse |
        Where-Object { $_.DirectoryName -like "*obj*runtime-locks" } |
        ForEach-Object {
            $ProjectName = $_.Directory.Parent.Parent.Name
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $RuntimeLockOutput "$ProjectName.packages.lock.json")
        }
}

Write-Host "Published CLI builds: $OutputRoot"
