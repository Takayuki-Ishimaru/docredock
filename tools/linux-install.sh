#!/bin/sh
set -eu

script_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
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
    { echo "DocRedock installation requires GNU realpath." >&2; exit 2; }
  lexical=$(realpath -ms -- "$1") ||
    { echo "Cannot normalize install prefix: $1" >&2; exit 2; }
  physical=$(realpath -m -- "$1") ||
    { echo "Cannot resolve install prefix: $1" >&2; exit 2; }
  [ "$lexical" = "$physical" ] ||
    { echo "Refusing symlinked install path: $1" >&2; exit 2; }
  case "$physical" in ""|"/") echo "Refusing unsafe install prefix: $1" >&2; exit 2 ;; esac
  printf '%s\n' "$physical"
}

require_safe_destination() {
  lexical=$(realpath -ms -- "$1") ||
    { echo "Cannot normalize install destination: $1" >&2; exit 2; }
  physical=$(realpath -m -- "$1") ||
    { echo "Cannot resolve install destination: $1" >&2; exit 2; }
  [ "$lexical" = "$physical" ] ||
    { echo "Refusing symlinked install destination: $1" >&2; exit 2; }
}

install_managed_launcher() {
  launcher=$1
  target=$2
  if [ -L "$launcher" ]; then
    existing_target=$(readlink "$launcher")
    [ "$existing_target" = "$target" ] ||
      { echo "Refusing to replace unmanaged launcher: $launcher" >&2; exit 2; }
    rm -f -- "$launcher"
  elif [ -e "$launcher" ]; then
    echo "Refusing to replace unmanaged launcher: $launcher" >&2
    exit 2
  fi
  ln -s -- "$target" "$launcher"
}

prefix=$(canonicalize_prefix "$prefix")
application_directory="$prefix/lib/docredock"
binary_directory="$prefix/bin"
data_directory="$prefix/share"
applications_directory="$data_directory/applications"
icon_directory="$data_directory/icons/hicolor/256x256/apps"
desktop="$applications_directory/docredock.desktop"
icon="$icon_directory/docredock.png"
managed_marker="$application_directory/.docredock-managed"
managed_marker_value=DocRedock-managed-install-v1

# Validate every destination before the first mutation. Recheck directories after
# creation to catch pre-existing symlink components.
for destination in "$application_directory" "$binary_directory" "$applications_directory" "$icon_directory"   "$application_directory/DocRedock" "$application_directory/DocRedock.Cli" "$application_directory/uninstall.sh"   "$application_directory/docs" "$application_directory/release-docs" "$desktop" "$icon" "$managed_marker"; do
  require_safe_destination "$destination"
done
if [ -e "$desktop" ] && ! grep -Fq 'X-DocRedock-Managed=true' "$desktop"; then
  echo "Refusing to replace unmanaged desktop entry: $desktop" >&2
  exit 2
fi
if [ -e "$application_directory" ] &&
   { [ ! -f "$managed_marker" ] || [ "$(cat -- "$managed_marker")" != "$managed_marker_value" ]; }; then
  echo "Refusing to replace unmanaged application directory: $application_directory" >&2
  exit 2
fi
if { [ -e "$icon" ] || [ -L "$icon" ]; } &&
   { [ ! -f "$managed_marker" ] || [ "$(cat -- "$managed_marker")" != "$managed_marker_value" ]; }; then
  echo "Refusing to replace unmanaged icon: $icon" >&2
  exit 2
fi

mkdir -p "$application_directory" "$binary_directory" "$applications_directory" "$icon_directory"
for destination in "$application_directory" "$binary_directory" "$applications_directory" "$icon_directory"; do
  require_safe_destination "$destination"
done
printf '%s\n' "$managed_marker_value" > "$managed_marker"

install -m 755 "$script_directory/DocRedock" "$application_directory/DocRedock"
install -m 755 "$script_directory/DocRedock.Cli" "$application_directory/DocRedock.Cli"
install -m 755 "$script_directory/uninstall.sh" "$application_directory/uninstall.sh"
install_managed_launcher "$binary_directory/docredock" ../lib/docredock/DocRedock.Cli
install_managed_launcher "$binary_directory/docredock-gui" ../lib/docredock/DocRedock
if [ -f "$script_directory/share/icons/hicolor/256x256/apps/docredock.png" ]; then
  require_safe_destination "$icon"
  install -m 644 "$script_directory/share/icons/hicolor/256x256/apps/docredock.png" "$icon"
fi
for document in LICENSE THIRD-PARTY-NOTICES.txt SECURITY.md QUICKSTART.ja.md QUICKSTART.en.md; do
  if [ -f "$script_directory/$document" ]; then
    require_safe_destination "$application_directory/$document"
    install -m 644 "$script_directory/$document" "$application_directory/$document"
  fi
done
for directory in docs release-docs; do
  if [ -d "$script_directory/$directory" ]; then
    require_safe_destination "$application_directory/$directory"
    rm -rf -- "$application_directory/$directory"
    cp -R "$script_directory/$directory" "$application_directory/$directory"
  fi
done
require_safe_destination "$desktop"
cat > "$desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=DocRedock
Comment=Local-first Office to Markdown utility
TryExec=$binary_directory/docredock-gui
Exec=$binary_directory/docredock-gui
Icon=$icon
Terminal=false
Categories=Office;Utility;
X-DocRedock-Managed=true
DESKTOP
printf 'DocRedock installed in %s\n' "$prefix"
printf 'Add %s to PATH if it is not already available.\n' "$binary_directory"
