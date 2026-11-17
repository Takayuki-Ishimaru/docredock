# DocRedock Package Quick Start

Japanese guide: `QUICKSTART.ja.md` / English

The running version is always visible at the top of the app. Use `docredock --version` in the CLI.

## Launch

- On Windows, run `DocRedock.exe`. Use `docredock.cmd` for the CLI.
- On macOS, open `DocRedock.app`. Run `./docredock` in a terminal to use the CLI.
- On Linux, run `./DocRedock` from the extracted folder. Use `./docredock` for the CLI. Run `./install.sh` for a user-local installation and `./uninstall.sh` to remove it. The default prefix is `$HOME/.local`. You can also open a supported document from the file manager with DocRedock.

## Convert your first file

Drop a Word, Excel, PowerPoint, or PDF file into DocRedock. Choose **Readable HTML** (「閲覧用HTML」), select **Export** (「書き出す」), then select **Open HTML** (「HTMLを開く」) to see the result in your browser.

To try the CLI, open a terminal in the extracted folder. Replace `report.xlsx` with the path to your file.

```powershell
.\docredock.cmd export "report.xlsx" --format html -o "report.html"
```

```sh
./docredock export "report.xlsx" --format html -o "report.html"
```

Use the first command in Windows PowerShell and the second on macOS or Linux. Add `--allow-pdf` for PDF input in the CLI. Choose **Final** in the Word revision settings or add `--revisions final` to exclude deleted text. Review the output and warnings before sharing.

## Use with AI assistants

You can set up Claude Code, GitHub Copilot and Codex to read Office documents through DocRedock first. In the GUI, use **AI連携** (AI integration) at the top. In the CLI, run `docredock agents install` (on Windows, `docredock.cmd agents install`). To remove it, run `docredock agents uninstall`. Restart the assistants after setting up. See the [quick guides](docs/en/quick-tasks.md).

## Update

The app checks non-draft published releases, including Public Beta builds, in the background at startup and shows the current and latest versions when an update exists. **Check for updates** performs a manual check. DocRedock never installs an update silently: open the release page, download the package for your OS/CPU, verify SHA-256 and signing status, then replace the prior installation. Set `DOCREDOCK_DISABLE_UPDATE_CHECK=1` to disable startup checks.

- [User guide](docs/en/user-guide.md)
- [Supported features](docs/en/supported-features.md)
- [Security and privacy](docs/en/security-and-privacy.md)
- [Changes in this release](release-docs/RELEASE_NOTES_v0.4.0.en.md)
