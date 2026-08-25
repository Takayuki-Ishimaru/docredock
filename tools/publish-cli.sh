#!/usr/bin/env sh
set -eu

script_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repository_root=$(CDPATH= cd -- "$script_directory/.." && pwd)
project="$repository_root/src/DocRedock.Cli/DocRedock.Cli.csproj"
output_root="$repository_root/artifacts/cli"

if [ "$#" -gt 0 ]; then
  runtime_ids="$*"
else
  runtime_ids="win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64"
fi

mkdir -p "$output_root"
for runtime_id in $runtime_ids; do
  case "$runtime_id" in
    win-x64|win-arm64|osx-x64|osx-arm64|linux-x64|linux-arm64) ;;
    *)
      echo "Unsupported runtime identifier: $runtime_id" >&2
      exit 2
      ;;
  esac

  echo "Publishing DocRedock CLI for $runtime_id"
  output_directory="$output_root/$runtime_id"
  case "$output_directory" in
    "$output_root"/*) rm -rf -- "$output_directory" ;;
    *) echo "Refusing to clear an unexpected output path." >&2; exit 2 ;;
  esac
  dotnet publish "$project" \
    --configuration Release \
    --runtime "$runtime_id" \
    --self-contained true \
    --output "$output_directory" \
    -p:PublishSingleFile=true \
    -p:UseAppHost=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:PublishTrimmed=false \
    -p:DebugType=None \
    "-p:NuGetLockFilePath=obj/publish-locks/$runtime_id/packages.lock.json" \
    -p:RestoreForceEvaluate=true \
    -p:RestoreLockedMode=false
done

echo "Published CLI builds: $output_root"
