# Security and Privacy

[日本語](../ja/security-and-privacy.md) | English

## Local processing and update checks

Built-in conversion runs locally. It does not upload documents, fetch external document URLs, execute spreadsheet formulas, or discover arbitrary plugins.

The GUI may send a short HTTPS request to the public GitHub Releases API for update metadata. It sends no document content, file name, or local path, and never auto-installs an update. Set `DOCREDOCK_DISABLE_UPDATE_CHECK=1` before launch to disable the request.

## Hidden content and sharing

Readable export defaults to `visible`, which filters recognized Office-hidden text, hidden/very-hidden sheets, hidden rows/columns, hidden slides/objects, notes, comments, and revisions out of the Markdown projection. `sanitized` additionally filters metadata, derived/OCR content, and document furniture. External `.assets/` output contains only images referenced by nodes included under the selected policy.

`complete` intentionally includes hidden and metadata content and emits `HiddenContentIncluded`. Treat its output as sensitive and review it before sharing. Visibility metadata can vary between Office producers, so no policy replaces human review.

Review both the `.md` and `.assets/`, as well as embedded data URIs, cached calculation results, and OCR output. Experimental `.drmd` and `.drmdpkg` files may include source binaries or restoration data and must be handled like the source document.

## Untrusted input

DocRedock applies limits and checks for XML DTDs, archive traversal/expansion, symlink escapes, suspicious formulas, and unsupported protected structures. Main limits are 200 MiB per source, 16 MiB per Markdown file, and 10 MiB per verified embedded image / 50 MiB total.

Process untrusted documents with isolated user privileges and apply your organization's malware controls.

## Output path protection

Every CLI command rejects an `--output` (or destination) path that refers to the same file as one of its own inputs -- the identical path, a case-only variant on a case-insensitive volume (macOS, Windows), a symlink to it, or a hard link identified as the same file -- even with `--force`. `--force` only replaces a stale previous output; it never authorizes overwriting the command's own source document. A colliding output fails with exit code 2 and writes nothing.

## Report a vulnerability

Do not post confidential reports or real documents in a public issue. Follow [SECURITY.md](../../SECURITY.md) and use a minimal synthetic reproducer.

Version 0.2.8 also protects the historical source during restoration. New sidecars retain the source's local absolute path in `source.original_path` (never sent over the network); consider this path metadata when sharing a sidecar. A destination matching that path/file identity or the source SHA-256 requires `--force --replace-original` and a retained backup. Older sidecars fall back to the source filename beside the Markdown and content hash. An older original moved elsewhere, renamed, and modified cannot be identified without path metadata. The current Markdown and immutable source inside the sidecar remain protected even with the dedicated replacement option.
