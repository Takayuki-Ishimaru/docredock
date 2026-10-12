# v0.3.4 external-review regressions

Synthetic DOCX fixtures from `DocRedock-v0.3.4-review-kit-20261012.zip`, supplied with the 2026-10-12 review. No customer documents are included.

- `cell_control.docx`: unchanged 2×2 table.
- `cell_one_cell.docx`: only row 2 cell 1 has `w:cellDel`; `KEPT_CELL` must survive final projection. Column correspondence after removal is uncertain and needs review.
- `cell_whole_column.docx`: both cells in column 1 have `w:cellDel`; the surviving column is rectangular.
- `edge-revisions.docx`: deleted text, moves, row/cell deletion, related parts, textbox and image histories.

Covered by `V034ReviewResponseTests`; these fixtures reproduce the supplied synthetic cases, not every Office revision pattern.
