# Technique provenance

The Phase 0–7 implementation is original code derived from the architectural
requirements in `DRMD_Architecture_Design_v0.2.md`. No source code from the
reference implementations listed by that document has been copied or adapted.

When a reference implementation is evaluated or code is incorporated, record
the project, source URL, pinned commit, license, adopted technique, and local
changes here before merging it.

Adobe-Japan1-UCS2 (Supplement 7, version 10.002) mapping data is incorporated
from adobe-type-tools/mapping-resources-pdf, revision
`2dd5e53fb74a01718b9dfd448a0d1cce6fff2aa5` (BSD-3-Clause).
`tools/generate-pdf-japan1.py` verifies its SHA-256 and generates compressed
C# data. Identity-H/V Adobe-Japan1 fonts without ToUnicode use this standard
CID-to-Unicode map. Custom CMaps and font programs are not incorporated.
The full data copyright, conditions and disclaimer are retained in
`THIRD-PARTY-NOTICES.txt`; `licenses/allowlist.json` records the source and hash.
