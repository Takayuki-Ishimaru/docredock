# Experimental Features

[日本語](../ja/experimental-features.md) | English

> These workflows are unsupported experiments in v0.1.4. They are disabled unless you explicitly opt in.

Set the environment variable before starting the GUI or CLI:

```sh
export DOCREDOCK_ENABLE_EXPERIMENTAL=1
```

PowerShell:

```powershell
$env:DOCREDOCK_ENABLE_EXPERIMENTAL = "1"
```

The distributed GUI/CLI gate applies to round-trip/audit export, restore, render, diff, rebase, pack, unpack, migrate, and PDF paths. Public library APIs are engineering surfaces and do not enforce this entry-point environment gate. Readable DOCX/XLSX/PPTX export remains available without the flag.

Experimental artifacts such as `.drmd` and `.drmdpkg` may contain the original source or restoration data. They require the same confidentiality controls as the source. F0/F1 tests and package smoke checks are engineering evidence, not a promise of layout-preserving user support.

Use [Supported features](supported-features.md) for the release contract and [Security and privacy](security-and-privacy.md) for handling guidance.
