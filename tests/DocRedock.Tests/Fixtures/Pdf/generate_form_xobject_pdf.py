#!/usr/bin/env python3
"""Generate the Form XObject twins: the same page drawn directly and through
Form XObjects.

A Form XObject is a reusable drawing component inside a PDF (not an
interactive AcroForm). Viewers render these files identically, so a text
extractor that reads only the page content stream silently loses everything
the form draws. That is the defect these fixtures pin down.

  form-xobject-flat.pdf    table + lower block painted on the page itself
  form-xobject-form.pdf    same table; lower block drawn by one Form XObject
  form-xobject-nested.pdf  same table; lower block drawn by a form that draws
                           another form under a translation, so the extractor
                           must compose the page CTM, both /Matrix entries and
                           the nested resource scope to land on the same
                           coordinates as the flat file
  form-xobject-flat-review.pdf / form-xobject-form-review.pdf
                           the flat and form layouts plus one stray diagonal
                           line in the lower block, which DocRedock cannot
                           resolve: the form file must keep the same warning
                           and source-review page as the flat file
  form-xobject-unreadable.pdf
                           the form layout with the form's stream re-encoded
                           as /RunLengthDecode, a valid filter DocRedock does
                           not decode. Viewers still paint the lower block, so
                           the export must warn and ask for source review
                           instead of reporting a complete page

Usage:
  python3 generate_form_xobject_pdf.py [output-directory]

The output is deterministic (``invariant=1``) and uses the Base14 Helvetica
font only, so no system font is required.
"""
from __future__ import annotations

import sys
from pathlib import Path

from pypdf import PdfReader, PdfWriter
from pypdf.generic import NameObject, NumberObject
from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas

PAGE_W, PAGE_H = A4
SENTINEL = "FORM_TEXT_SENTINEL: approved"
NESTED_SHIFT = (40.0, 25.0)


def draw_table(c: canvas.Canvas) -> None:
    """A ruled 3x3 table with a header row, drawn directly on the page."""
    c.setFont("Helvetica-Bold", 14)
    c.drawString(72, 780, "Quarterly review")
    left, top = 72.0, 760.0
    widths = [150.0, 150.0, 150.0]
    height = 24.0
    rows = [("Item", "Owner", "Status"), ("Design", "Aoki", "Done"), ("Build", "Sato", "Open")]
    c.setLineWidth(1)
    total_w = sum(widths)
    for index in range(len(rows) + 1):
        y = top - index * height
        c.line(left, y, left + total_w, y)
    x = left
    for width in [0.0] + widths:
        x += width
        c.line(x, top, x, top - len(rows) * height)
    for row_index, row in enumerate(rows):
        c.setFont("Helvetica-Bold" if row_index == 0 else "Helvetica", 11)
        x = left
        for width, value in zip(widths, row):
            c.drawString(x + 6, top - (row_index + 1) * height + 8, value)
            x += width


def draw_lower_block(c: canvas.Canvas, dx: float = 0.0, dy: float = 0.0, diagonal: bool = False) -> None:
    """The content the form variants move into Form XObjects: a sentinel sentence, two labelled
    boxes joined by an arrow, and one free-standing label. ``dx``/``dy`` pre-compensate a
    translation applied by the caller, so the painted result never moves. ``diagonal`` adds a
    stray diagonal line that connects nothing."""
    c.setFont("Helvetica", 12)
    c.drawString(72 - dx, 520 - dy, SENTINEL)
    c.setLineWidth(1.5)
    c.rect(100 - dx, 400 - dy, 110, 44, stroke=1, fill=0)
    c.rect(360 - dx, 400 - dy, 110, 44, stroke=1, fill=0)
    c.setFont("Helvetica", 12)
    c.drawCentredString(155 - dx, 418 - dy, "START")
    c.drawCentredString(415 - dx, 418 - dy, "END")
    c.line(210 - dx, 422 - dy, 350 - dx, 422 - dy)
    arrow = c.beginPath()
    arrow.moveTo(360 - dx, 422 - dy)
    arrow.lineTo(350 - dx, 427 - dy)
    arrow.lineTo(350 - dx, 417 - dy)
    arrow.close()
    c.drawPath(arrow, stroke=1, fill=1)
    c.setFont("Helvetica", 10)
    c.drawString(250 - dx, 340 - dy, "UNASSIGNED_LABEL")
    if diagonal:
        c.line(480 - dx, 300 - dy, 540 - dx, 360 - dy)


def build(path: Path, variant: str, diagonal: bool = False) -> None:
    c = canvas.Canvas(str(path), pagesize=A4, invariant=1)
    c.setTitle(f"DocRedock Form XObject fixture ({variant})")
    if variant == "form":
        c.beginForm("LowerBlock")
        draw_lower_block(c, diagonal=diagonal)
        c.endForm()
    elif variant == "nested":
        dx, dy = NESTED_SHIFT
        c.beginForm("LowerInner")
        draw_lower_block(c, dx, dy)
        c.endForm()
        c.beginForm("LowerOuter")
        c.saveState()
        c.translate(dx, dy)
        c.doForm("LowerInner")
        c.restoreState()
        c.endForm()
    draw_table(c)
    if variant == "flat":
        draw_lower_block(c, diagonal=diagonal)
    elif variant == "form":
        c.doForm("LowerBlock")
    else:
        c.doForm("LowerOuter")
    c.showPage()
    c.save()


def run_length_encode(data: bytes) -> bytes:
    """PDF RunLengthDecode encoding using literal runs only (always valid, never compact)."""
    out = bytearray()
    for start in range(0, len(data), 128):
        chunk = data[start:start + 128]
        out.append(len(chunk) - 1)
        out += chunk
    out.append(128)
    return bytes(out)


def reencode_form_as_run_length(source: Path, target: Path) -> None:
    """Rewrites the page's one Form XObject with /RunLengthDecode. The painted page is unchanged;
    only the encoding of the form's content stream differs."""
    writer = PdfWriter(clone_from=PdfReader(str(source)))
    xobjects = writer.pages[0]["/Resources"]["/XObject"]
    forms = [xobjects[name].get_object() for name in xobjects if xobjects[name].get_object().get("/Subtype") == "/Form"]
    assert len(forms) == 1, forms
    form = forms[0]
    encoded = run_length_encode(form.get_data())
    form._data = encoded  # already encoded bytes; written as-is
    form[NameObject("/Filter")] = NameObject("/RunLengthDecode")
    form[NameObject("/Length")] = NumberObject(len(encoded))
    if "/DecodeParms" in form:
        del form["/DecodeParms"]
    with target.open("wb") as handle:
        writer.write(handle)


def main(argv: list[str]) -> int:
    out_dir = Path(argv[1]) if len(argv) > 1 else Path(__file__).resolve().parent
    out_dir.mkdir(parents=True, exist_ok=True)
    targets = []
    for variant in ("flat", "form", "nested"):
        target = out_dir / f"form-xobject-{variant}.pdf"
        build(target, variant)
        targets.append(target)
    for variant in ("flat", "form"):
        target = out_dir / f"form-xobject-{variant}-review.pdf"
        build(target, variant, diagonal=True)
        targets.append(target)
    unreadable = out_dir / "form-xobject-unreadable.pdf"
    reencode_form_as_run_length(out_dir / "form-xobject-form.pdf", unreadable)
    targets.append(unreadable)
    for target in targets:
        print(f"wrote {target} ({target.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
