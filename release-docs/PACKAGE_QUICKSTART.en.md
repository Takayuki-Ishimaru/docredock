# DocRedock Package Quick Start

Japanese guide: `QUICKSTART.ja.md` / English

The running version is always visible at the top of the app. Use `docredock --version` in the CLI.

## Launch

- Windows: run `DocRedock.exe`; use `docredock.cmd` for the CLI.
- macOS: run `DocRedock.app`; use `./docredock` in a terminal.
- Linux: run `./DocRedock` after extraction and use `./docredock` for the CLI. Run `./install.sh` for a user-local installation and `./uninstall.sh` to remove it. The default prefix is `$HOME/.local`.

## Update

The app checks non-draft published releases, including Public Beta builds, in the background at startup and shows the current and latest versions when an update exists. **Check for updates** performs a manual check. DocRedock never installs an update silently: open the release page, download the package for your OS/CPU, verify SHA-256 and signing status, then replace the prior installation. Set `DOCREDOCK_DISABLE_UPDATE_CHECK=1` to disable startup checks.

- [User guide](docs/en/user-guide.md)
- [Supported features](docs/en/supported-features.md)
- [Security and privacy](docs/en/security-and-privacy.md)
- [Changes in this release](release-docs/RELEASE_NOTES_v0.2.2.en.md)
