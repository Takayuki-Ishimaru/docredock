#!/usr/bin/env python3
"""Generate the font-scope and visibility fixtures of the v0.2.11 evaluation.

Every font here is Base14 Helvetica with an /Encoding /Differences that draws
the code of "A" as another letter, plus a /ToUnicode CMap saying the same.
That is what a subset font does: the codes in the content stream are glyph
slots, and only the font's own maps say which character each one is. Viewers
and pdftotext therefore show exactly the letters the tests expect.

  font-scope-forms.pdf   the page and two Form XObjects each call a different
                         font /F1; the page shows XXX, the forms YYY and ZZZ.
                         One document-wide name table read all three as ZZZ.
  font-scope-flat.pdf    the same page drawn directly with /F1 /F2 /F3 - the
                         control that must export identically
  font-scope-inline.pdf  the control again with each font dictionary written
                         inline in the page's /Font dictionary; its ToUnicode
                         was never found, and the page came out as AAA
  form-bbox-clipped.pdf  a form whose /BBox holds VISIBLE_TEXT while it also
                         draws CLIPPED_SENTINEL outside the box, where no
                         viewer shows it
  layer-off.pdf          text on two optional content groups (layers), one of
                         which is off when the document opens

Usage:
  python3 generate_font_scope_pdf.py [output-directory]

Only the Python standard library is needed, and the output is deterministic.
"""
from __future__ import annotations

import sys
from pathlib import Path

LETTERS = {10: "X", 12: "Y", 14: "Z"}


def stream(dictionary: str, data: str) -> bytes:
    payload = data.encode("latin-1")
    return f"<< {dictionary} /Length {len(payload)} >>\nstream\n".encode("latin-1") + payload + b"\nendstream"


def cmap(letter: str) -> bytes:
    return stream("", "\n".join([
        "/CIDInit /ProcSet findresource begin",
        "12 dict begin",
        "begincmap",
        "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def",
        "/CMapName /Adobe-Identity-UCS def",
        "/CMapType 2 def",
        "1 begincodespacerange",
        "<00> <FF>",
        "endcodespacerange",
        "1 beginbfchar",
        f"<41> <{ord(letter):04X}>",
        "endbfchar",
        "endcmap",
        "CMapName currentdict /CMap defineresource pop",
        "end",
        "end",
    ]))


def font_dictionary(letter: str, cmap_id: int) -> str:
    return (f"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding "
            f"/BaseEncoding /WinAnsiEncoding /Differences [65 /{letter}] >> /ToUnicode {cmap_id} 0 R >>")


def lettered_fonts() -> dict[int, bytes]:
    objects: dict[int, bytes] = {}
    for font_id, letter in LETTERS.items():
        objects[font_id] = font_dictionary(letter, font_id + 1).encode("latin-1")
        objects[font_id + 1] = cmap(letter)
    return objects


def page(resources: str, content: str, catalog_extra: str = "") -> dict[int, bytes]:
    return {
        1: f"<< /Type /Catalog /Pages 2 0 R{catalog_extra} >>".encode("latin-1"),
        2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        3: (f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            f"/Resources << {resources} >> >>").encode("latin-1"),
        4: stream("", content),
    }


def form(resources: str, content: str, bbox: str = "0 0 612 792", extra: str = "") -> bytes:
    return stream(f"/Type /XObject /Subtype /Form /BBox [{bbox}] /Resources << {resources} >>{extra}", content)


def write_pdf(path: Path, objects: dict[int, bytes]) -> None:
    out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets: dict[int, int] = {}
    for object_id in sorted(objects):
        offsets[object_id] = len(out)
        out += f"{object_id} 0 obj\n".encode("latin-1") + objects[object_id] + b"\nendobj\n"
    xref = len(out)
    size = max(objects) + 1
    out += f"xref\n0 {size}\n".encode("latin-1") + b"0000000000 65535 f \n"
    for object_id in range(1, size):
        out += (f"{offsets[object_id]:010d} 00000 n \n" if object_id in offsets else "0000000000 65535 f \n").encode("latin-1")
    out += f"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode("latin-1")
    path.write_bytes(bytes(out))


def line(font: str, y: int) -> str:
    return f"BT /{font} 24 Tf 72 {y} Td (AAA) Tj ET"


def main() -> None:
    output = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent
    output.mkdir(parents=True, exist_ok=True)

    forms = page("/Font << /F1 10 0 R >> /XObject << /Fm1 20 0 R /Fm2 21 0 R >>",
                 line("F1", 700) + "\n/Fm1 Do\n/Fm2 Do")
    forms.update(lettered_fonts())
    forms[20] = form("/Font << /F1 12 0 R >>", line("F1", 650))
    forms[21] = form("/Font << /F1 14 0 R >>", line("F1", 600))
    write_pdf(output / "font-scope-forms.pdf", forms)

    flat = page("/Font << /F1 10 0 R /F2 12 0 R /F3 14 0 R >>",
                "\n".join([line("F1", 700), line("F2", 650), line("F3", 600)]))
    flat.update(lettered_fonts())
    write_pdf(output / "font-scope-flat.pdf", flat)

    inline_fonts = " ".join(f"/F{index} {font_dictionary(letter, font_id + 1)}"
                            for index, (font_id, letter) in enumerate(LETTERS.items(), start=1))
    inline = page(f"/Font << {inline_fonts} >>", "\n".join([line("F1", 700), line("F2", 650), line("F3", 600)]))
    for font_id, letter in LETTERS.items():
        inline[font_id + 1] = cmap(letter)
    write_pdf(output / "font-scope-inline.pdf", inline)

    helvetica = "/Font << /F1 10 0 R >>"
    clipped = page(helvetica + " /XObject << /Fm1 20 0 R >>",
                   "BT /F1 12 Tf 72 720 Td (PAGE_TEXT) Tj ET\nq 1 0 0 1 100 400 cm /Fm1 Do Q")
    clipped[10] = b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
    clipped[20] = form(helvetica,
                       "BT /F1 12 Tf 10 40 Td (VISIBLE_TEXT) Tj ET\n"
                       "BT /F1 12 Tf 10 160 Td (CLIPPED_SENTINEL) Tj ET", bbox="0 0 300 100")
    write_pdf(output / "form-bbox-clipped.pdf", clipped)

    layers = page(helvetica + " /Properties << /L1 30 0 R /L2 31 0 R >>",
                  "BT /F1 12 Tf 72 720 Td (PAGE_TEXT) Tj ET\n"
                  "/OC /L1 BDC BT /F1 12 Tf 72 690 Td (SHOWN_LAYER) Tj ET EMC\n"
                  "/OC /L2 BDC BT /F1 12 Tf 72 660 Td (HIDDEN_LAYER) Tj ET EMC",
                  " /OCProperties << /OCGs [30 0 R 31 0 R] /D << /Order [30 0 R 31 0 R] /OFF [31 0 R] >> >>")
    layers[10] = b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
    layers[30] = b"<< /Type /OCG /Name (Shown) >>"
    layers[31] = b"<< /Type /OCG /Name (Hidden) >>"
    write_pdf(output / "layer-off.pdf", layers)


if __name__ == "__main__":
    main()
