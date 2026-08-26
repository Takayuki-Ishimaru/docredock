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
  runtime_lock_path="obj/runtime-locks/packages.$runtime_id.lock.json"
  dotnet restore "$project" --runtime "$runtime_id" --force-evaluate --use-lock-file --lock-file-path "$runtime_lock_path"
  dotnet restore "$project" --runtime "$runtime_id" --locked-mode --use-lock-file --lock-file-path "$runtime_lock_path"
  dotnet publish "$project" \
    --configuration Release \
    --runtime "$runtime_id" \
    --self-contained true \
    --no-restore \
    --output "$output_directory" \
    -p:PublishSingleFile=true \
    -p:UseAppHost=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=false \
    -p:PublishTrimmed=false \
    -p:DebugType=None

  runtime_lock_output="$output_directory/runtime-locks"
  mkdir -p "$runtime_lock_output"
  find "$repository_root/src" -path "*/obj/runtime-locks/packages.$runtime_id.lock.json" -type f | while IFS= read -r lock_file; do
    project_directory=$(dirname "$(dirname "$(dirname "$lock_file")")")
    project_name=$(basename "$project_directory")
    cp "$lock_file" "$runtime_lock_output/$project_name.packages.lock.json"
  done
done

echo "Published CLI builds: $output_root"
