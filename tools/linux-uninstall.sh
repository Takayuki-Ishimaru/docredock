#!/bin/sh
set -eu

prefix=${DOCREDOCK_INSTALL_PREFIX:-"$HOME/.local"}
while [ "$#" -gt 0 ]; do
  case "$1" in
    --prefix)
      [ "$#" -ge 2 ] || { echo "--prefix requires a directory" >&2; exit 2; }
      prefix=$2
      shift 2
      ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

canonicalize_prefix() {
  command -v realpath >/dev/null 2>&1 ||
    { echo "DocRedock uninstallation requires GNU realpath." >&2; exit 2; }
  lexical=$(realpath -ms -- "$1") ||
    { echo "Cannot normalize uninstall prefix: $1" >&2; exit 2; }
  physical=$(realpath -m -- "$1") ||
    { echo "Cannot resolve uninstall prefix: $1" >&2; exit 2; }
  [ "$lexical" = "$physical" ] ||
    { echo "Refusing symlinked uninstall path: $1" >&2; exit 2; }
  case "$physical" in ""|"/") echo "Refusing unsafe uninstall prefix: $1" >&2; exit 2 ;; esac
  printf '%s\n' "$physical"
}

require_safe_managed_path() {
  lexical=$(realpath -ms -- "$1") ||
    { echo "Cannot normalize uninstall path: $1" >&2; exit 2; }
  physical=$(realpath -m -- "$1") ||
    { echo "Cannot resolve uninstall path: $1" >&2; exit 2; }
  [ "$lexical" = "$physical" ] ||
    { echo "Refusing symlinked uninstall path: $1" >&2; exit 2; }
}

prefix=$(canonicalize_prefix "$prefix")
application_directory="$prefix/lib/docredock"
binary_directory="$prefix/bin"
applications_directory="$prefix/share/applications"
icon_directory="$prefix/share/icons/hicolor/256x256/apps"
desktop="$applications_directory/docredock.desktop"
icon="$icon_directory/docredock.png"
managed_marker="$application_directory/.docredock-managed"
managed_marker_value=DocRedock-managed-install-v1

# Complete preflight before deleting anything so a hostile component cannot turn
# a partial uninstall into writes outside the selected prefix.
for managed_path in "$application_directory" "$binary_directory" "$applications_directory" "$icon_directory"   "$application_directory/docs" "$application_directory/release-docs" "$desktop" "$icon" "$managed_marker"; do
  require_safe_managed_path "$managed_path"
done
if [ ! -e "$application_directory" ]; then
  printf 'No DocRedock managed installation found in %s\n' "$prefix"
  exit 0
fi
if [ ! -f "$managed_marker" ] || [ "$(cat -- "$managed_marker")" != "$managed_marker_value" ]; then
  echo "Refusing to uninstall an unmarked or tampered application directory: $application_directory" >&2
  exit 2
fi

for launcher in "$binary_directory/docredock" "$binary_directory/docredock-gui"; do
  if [ -L "$launcher" ]; then
    target=$(readlink "$launcher")
    case "$target" in ../lib/docredock/*) rm -f -- "$launcher" ;; *) echo "Leaving unmanaged launcher: $launcher" >&2 ;; esac
  fi
done
if [ -f "$desktop" ] && grep -Fq 'X-DocRedock-Managed=true' "$desktop"; then rm -f -- "$desktop"; fi
rm -f -- "$icon"
for file in DocRedock DocRedock.Cli uninstall.sh LICENSE THIRD-PARTY-NOTICES.txt SECURITY.md QUICKSTART.ja.md QUICKSTART.en.md; do
  rm -f -- "$application_directory/$file"
done
rm -rf -- "$application_directory/docs" "$application_directory/release-docs"
rm -f -- "$managed_marker"
rmdir "$application_directory" 2>/dev/null || true
printf 'DocRedock managed files removed from %s\n' "$prefix"
