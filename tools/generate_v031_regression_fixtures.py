#!/usr/bin/env python3
"""Regenerate the eight V030 evaluation regression fixtures synthetically.

Usage: python3 tools/generate_v031_regression_fixtures.py --output DIR

Only the Python standard library is used. Output is deterministic: repeated
runs write byte-identical files. No embedded fonts, no third-party media.
"""
import argparse
from pathlib import Path
import struct
import sys
import zipfile
import zlib

FIXTURE_NAMES = (
    "workbook.xlsx",
    "workbook_base.xlsx",
    "twocol.pdf",
    "onecol.pdf",
    "image_space.docx",
    "image_nospace.docx",
    "scale500.xlsx",
    "chunking.docx",
)

ZIP_TIMESTAMP = (2026, 10, 4, 0, 0, 0)


def esc(text):
    return (text.replace("&", "&amp;").replace("<", "&lt;")
                .replace(">", "&gt;").replace('"', "&quot;"))


def solid_png(width=64, height=18, rgba=(31, 119, 180, 255)):
    """Minimal valid RGB 8-bit PNG filled with one solid color."""
    def chunk(tag, data):
        body = tag + data
        return struct.pack("!I", len(data)) + body + struct.pack("!I", zlib.crc32(body))
    raw = b"".join(b"\x00" + bytes(rgba[:3]) * width for _ in range(height))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack("!IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def write_zip(path, entries):
    """entries: list of (name, bytes); fixed timestamps/order for determinism."""
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as zf:
        for name, data in entries:
            info = zipfile.ZipInfo(name, date_time=ZIP_TIMESTAMP)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o600 << 16
            zf.writestr(info, data)


def col_name(index):
    name = ""
    index += 1
    while index:
        index, rem = divmod(index - 1, 26)
        name = chr(ord("A") + rem) + name
    return name


def validate_cell(ref, value):
    if not isinstance(ref, str) or not ref:
        raise ValueError("cell reference must be a non-empty string")
    digits = 0
    for ch in ref:
        if ch.isdigit():
            break
        if not ch.isalpha():
            raise ValueError("bad cell reference: %r" % (ref,))
        digits += 1
    if digits == 0 or not ref[digits:].isdigit():
        raise ValueError("bad cell reference: %r" % (ref,))
    if value is None:
        raise ValueError("cell %s has no value; omit it instead" % ref)
    if isinstance(value, bool) or not isinstance(value, (int, float, str)):
        raise ValueError("unsupported cell type for %s: %s" % (ref, type(value).__name__))


X_NS = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
R_NS = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
PKG_REL_NS = "http://schemas.openxmlformats.org/package/2006/relationships"
CT_NS = "http://schemas.openxmlformats.org/package/2006/content-types"


def _cell_xml(ref, value, style):
    s = ' s="%d"' % style if style else ""
    if isinstance(value, str):
        return '<c r="%s"%s t="inlineStr"><is><t xml:space="preserve">%s</t></is></c>' % (
            ref, s, esc(value))
    return '<c r="%s"%s><v>%s</v></c>' % (ref, s, repr(value) if isinstance(value, float) else value)


def sheet_xml(rows, merges=(), cols=(), sheet_format=None, tab_color=None):
    """rows: list of (row_number, [(cell_ref, value, style), ...])."""
    parts = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
             '<worksheet xmlns="%s" xmlns:r="%s">' % (X_NS, R_NS)]
    if tab_color:
        parts.append('<sheetPr><tabColor rgb="%s"/></sheetPr>' % tab_color)
    if sheet_format:
        parts.append('<sheetFormatPr %s/>' % sheet_format)
    if cols:
        parts.append("<cols>" + "".join(cols) + "</cols>")
    parts.append("<sheetData>")
    for row_number, cells in rows:
        parts.append('<row r="%d">' % row_number)
        for ref, value, style in cells:
            validate_cell(ref, value)
            parts.append(_cell_xml(ref, value, style))
        parts.append("</row>")
    parts.append("</sheetData>")
    if merges:
        parts.append('<mergeCells count="%d">' % len(merges)
                     + "".join('<mergeCell ref="%s"/>' % m for m in merges)
                     + "</mergeCells>")
    parts.append('<pageMargins left="0.7" right="0.7" top="0.75" bottom="0.75" '
                 'header="0.3" footer="0.3"/>')
    parts.append("</worksheet>")
    return "".join(parts).encode("utf-8")


def workbook_zip(path, sheets):
    """sheets: list of dicts with name, rows, optional merges/cols/tab_color/hidden."""
    n = len(sheets)
    workbook = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
                '<workbook xmlns="%s" xmlns:r="%s"><sheets>' % (X_NS, R_NS)]
    rels = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
            '<Relationships xmlns="%s">' % PKG_REL_NS]
    types = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
             '<Types xmlns="%s">' % CT_NS,
             '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>',
             '<Default Extension="xml" ContentType="application/xml"/>',
             '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>',
             '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>']
    entries = []
    for i, sheet in enumerate(sheets, start=1):
        state = ' state="hidden"' if sheet.get("hidden") else ""
        workbook.append('<sheet name="%s" sheetId="%d"%s r:id="rId%d"/>'
                        % (esc(sheet["name"]), i, state, i))
        rels.append('<Relationship Id="rId%d" Type="%s/worksheet" Target="worksheets/sheet%d.xml"/>'
                    % (i, R_NS, i))
        types.append('<Override PartName="/xl/worksheets/sheet%d.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>' % i)
        sheet_body = sheet_xml(sheet["rows"], merges=sheet.get("merges", ()),
                               cols=sheet.get("cols", ()),
                               sheet_format=sheet.get("sheet_format"),
                               tab_color=sheet.get("tab_color"))
        entries.append(("xl/worksheets/sheet%d.xml" % i, sheet_body))
    workbook.append("</sheets></workbook>")
    rels.append('<Relationship Id="rId%d" Type="%s/styles" Target="styles.xml"/>' % (n + 1, R_NS))
    rels.append("</Relationships>")
    types.append("</Types>")
    root_rels = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                 '<Relationships xmlns="%s">'
                 '<Relationship Id="rId1" Type="%s/officeDocument" Target="xl/workbook.xml"/>'
                 "</Relationships>" % (PKG_REL_NS, R_NS)).encode("utf-8")
    styles = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
              '<styleSheet xmlns="%s">'
              '<fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>'
              '<fills count="1"><fill><patternFill patternType="none"/></fill></fills>'
              '<borders count="1"><border/></borders>'
              '<cellStyleXfs count="1"><xf/></cellStyleXfs>'
              '<cellXfs count="1"><xf/></cellXfs>'
              "</styleSheet>" % X_NS).encode("utf-8")
    entries = [("xl/workbook.xml", "".join(workbook).encode("utf-8")),
               ("xl/_rels/workbook.xml.rels", "".join(rels).encode("utf-8")),
               ("xl/styles.xml", styles)] + entries + [
               ("_rels/.rels", root_rels),
               ("[Content_Types].xml", "".join(types).encode("utf-8"))]
    write_zip(path, entries)


PDF_TEXTS = (
    "Revenue policy.",
    "Revenue is recognized",
    "when delivery is complete.",
    "Refund policy.",
    "Refunds are accepted",
    "within thirty days.",
)


def make_pdf(columns):
    """One A4 page, six Helvetica paragraphs; columns=1 or 2 column layout."""
    if columns not in (1, 2):
        raise ValueError("columns must be 1 or 2")
    lines = []
    if columns == 2:
        for i, text in enumerate(PDF_TEXTS):
            x = 40 if i < 3 else 315
            y = 761.8898 - (i % 3) * 20
            lines.append((x, y, text))
    else:
        for i, text in enumerate(PDF_TEXTS):
            y = 761.8898 - i * 20 - (i // 3) * 30
            lines.append((40.0, y, text))
    content = ["BT /F1 11 Tf 13.2 TL"]
    for x, y, text in lines:
        content.append("1 0 0 1 %.4f %.4f Tm (%s) Tj T*" % (x, y, text.replace("(", "\\(").replace(")", "\\)")))
    content.append("ET")
    stream = "\n".join(content).encode("ascii")
    objects = [
        b"<<\n/Type /Catalog /Pages 2 0 R\n>>",
        b"<<\n/Type /Pages /Kids [3 0 R] /Count 1\n>>",
        (b"<<\n/Type /Page /Parent 2 0 R /MediaBox [0 0 595.2756 841.8898]\n"
         b"/Resources <<\n/Font <<\n/F1 4 0 R\n>>\n>>\n/Contents 5 0 R\n>>"),
        (b"<<\n/Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding\n>>"),
        b"<<\n/Length " + str(len(stream)).encode("ascii") + b"\n>>\nstream\n"
        + stream + b"\nendstream",
    ]
    out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets = []
    for i, body in enumerate(objects, start=1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i + body + b"\nendobj\n"
    xref_pos = len(out)
    out += b"xref\n0 %d\n" % (len(objects) + 1)
    out += b"0000000000 65535 f \n"
    for off in offsets:
        out += b"%010d 00000 n \n" % off
    out += (b"trailer\n<<\n/Size %d /Root 1 0 R\n>>\nstartxref\n%d\n%%%%EOF\n"
            % (len(objects) + 1, xref_pos))
    return bytes(out)


W_NS = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"


def paragraph(text, style=None, numbered=False):
    properties = '<w:pStyle w:val="%s"/>' % style if style else ""
    if numbered:
        properties += '<w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr>'
    return ('<w:p><w:pPr>' + properties + '</w:pPr><w:r><w:t xml:space="preserve">'
            + esc(text) + '</w:t></w:r></w:p>')


def table(rows):
    return ('<w:tbl><w:tblPr/><w:tblGrid><w:gridCol w:w="2400"/>'
            '<w:gridCol w:w="2400"/></w:tblGrid>'
            + ''.join('<w:tr>' + ''.join('<w:tc><w:tcPr/>' + paragraph(cell)
                                       + '</w:tc>' for cell in row) + '</w:tr>' for row in rows)
            + '</w:tbl>')


def word_zip(path, body, image=False):
    namespaces = (f'xmlns:w="{W_NS}" xmlns:r="{R_NS}" '
                  'xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" '
                  'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" '
                  'xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"')
    document = (f'<w:document {namespaces}><w:body>{body}<w:sectPr>'
                '<w:pgSz w:w="11906" w:h="16838"/>'
                '<w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440"/>'
                '</w:sectPr></w:body></w:document>')
    styles = (f'<w:styles xmlns:w="{W_NS}"><w:style w:type="paragraph" w:styleId="Normal">'
              '<w:name w:val="Normal"/></w:style><w:style w:type="paragraph" w:styleId="Heading1">'
              '<w:name w:val="heading 1"/><w:basedOn w:val="Normal"/>'
              '<w:pPr><w:outlineLvl w:val="0"/></w:pPr></w:style></w:styles>')
    numbering = (f'<w:numbering xmlns:w="{W_NS}"><w:abstractNum w:abstractNumId="0">'
                 '<w:multiLevelType w:val="singleLevel"/><w:lvl w:ilvl="0">'
                 '<w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/>'
                 '</w:lvl></w:abstractNum><w:num w:numId="1"><w:abstractNumId w:val="0"/>'
                 '</w:num></w:numbering>')
    rels = (f'<Relationships xmlns="{PKG_REL_NS}">'
            f'<Relationship Id="styles" Type="{R_NS}/styles" Target="styles.xml"/>'
            f'<Relationship Id="numbering" Type="{R_NS}/numbering" Target="numbering.xml"/>')
    if image:
        rels += f'<Relationship Id="image" Type="{R_NS}/image" Target="media/image1.png"/>'
    rels += '</Relationships>'
    types = (f'<Types xmlns="{CT_NS}">'
             '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
             '<Default Extension="xml" ContentType="application/xml"/>'
             '<Default Extension="png" ContentType="image/png"/>'
             '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
             '<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>'
             '<Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>'
             '</Types>')
    root_rels = (f'<Relationships xmlns="{PKG_REL_NS}"><Relationship Id="document" '
                 f'Type="{R_NS}/officeDocument" Target="word/document.xml"/></Relationships>')
    entries = [(name, value.encode('utf-8')) for name, value in [
        ('[Content_Types].xml', types), ('_rels/.rels', root_rels),
        ('word/document.xml', document), ('word/styles.xml', styles),
        ('word/numbering.xml', numbering), ('word/_rels/document.xml.rels', rels)]]
    if image:
        entries.append(('word/media/image1.png', solid_png()))
    write_zip(path, entries)


def image_document(path, trailing_space):
    drawing = ('<w:r><w:drawing><wp:inline distT="0" distB="0" distL="0" distR="0">'
               '<wp:extent cx="609600" cy="171450"/><wp:docPr id="1" name="Synthetic rectangle"/>'
               '<a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">'
               '<pic:pic><pic:nvPicPr><pic:cNvPr id="1" name="Synthetic rectangle"/>'
               '<pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="image"/>'
               '<a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr>'
               '<a:xfrm><a:off x="0" y="0"/><a:ext cx="609600" cy="171450"/></a:xfrm>'
               '<a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic>'
               '</a:graphicData></a:graphic></wp:inline></w:drawing></w:r>')
    caption = paragraph('Caption:' + (' ' if trailing_space else ''))
    word_zip(path, caption.removesuffix('</w:p>') + drawing + '</w:p>'
             + paragraph('Editable OLD'), image=True)


def revenue_workbook(path, hidden_notes):
    rows = [(1, [('A1', 'Financial summary', 0), ('H1', 'Candidates', 0), ('I1', 'Value', 0)]),
            (2, [('A2', 'Item', 0), ('B2', 'Q1', 0), ('D2', 'Q2', 0), ('F2', 'Notes', 0),
                 ('H2', 'CANDIDATE_ONE', 0), ('I2', 1, 0)]),
            (3, [('A3', 'Revenue', 0), ('B3', 100, 0), ('D3', 120, 0), ('F3', 'normal', 0),
                 ('H3', 'CANDIDATE_TWO', 0), ('I3', 2, 0)]),
            (4, [('A4', 'Cost', 0), ('B4', 70, 0), ('D4', 80, 0), ('F4', 'normal', 0)]),
            (5, [('A5', 'Total', 0), ('B5', 170, 0), ('D5', 200, 0), ('F5', 'final', 0)])]
    columns = ['<col min="6" max="6" width="18" customWidth="1" hidden="%d"/>'
               % (1 if hidden_notes else 0)]
    workbook_zip(path, [dict(name='Summary', rows=rows, merges=['A1:F1'], cols=columns)])


def scale_workbook(path):
    rows = [(1, [(col_name(i) + '1', label, 0) for i, label in enumerate(
        ['Item', 'Q1', 'Q2', 'Q3', 'Q4', 'Total'])])]
    for row in range(2, 502):
        rows.append((row, [(col_name(i) + str(row), 'Item %d' % (row - 1) if i == 0
                            else (row - 1) * i, 0) for i in range(6)]))
    workbook_zip(path, [dict(name='Data', rows=rows)])


def chunking_document(path):
    body = []
    for section in range(1, 5):
        body.append(paragraph('Section %d' % section, style='Heading1'))
        for item in range(2):
            body.append(paragraph(('S%dP%d synthetic body. ' % (section, item)) * 12))
        if section == 2:
            body.append(table([['Item', 'Value'], ['Synthetic table', '100']]))
        if section == 3:
            for item in range(8):
                body.append(paragraph(('Sequence %d list body. ' % item) * 12, numbered=True))
    word_zip(path, ''.join(body))


def generate(output):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    revenue_workbook(output / 'workbook.xlsx', hidden_notes=True)
    revenue_workbook(output / 'workbook_base.xlsx', hidden_notes=False)
    for columns, name in [(2, 'twocol.pdf'), (1, 'onecol.pdf')]:
        (output / name).write_bytes(make_pdf(columns))
    image_document(output / 'image_space.docx', True)
    image_document(output / 'image_nospace.docx', False)
    scale_workbook(output / 'scale500.xlsx')
    chunking_document(output / 'chunking.docx')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        generate(args.output)
    except (OSError, ValueError) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
