#!/usr/bin/env python3
"""Generate a one-slide editable flowchart sample PPTX for documentation.

Usage: python3 generate_sample.py [output.pptx]

Requires python-pptx==1.0.2 only. All content is synthetic; core properties
are set explicitly so no template author metadata leaks into the file.
"""
import sys
from pathlib import Path

from pptx import Presentation
from pptx.dml.color import RGBColor
from pptx.enum.shapes import MSO_CONNECTOR, MSO_SHAPE
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR
from pptx.oxml.ns import qn
from pptx.util import Inches, Pt

INK = RGBColor(0x33, 0x3F, 0x50)
LINE = RGBColor(0x5B, 0x6B, 0x7F)
BLUE_FILL = RGBColor(0xDD, 0xEA, 0xF7)
BLUE_LINE = RGBColor(0x44, 0x72, 0xC4)
GREEN_FILL = RGBColor(0xDC, 0xF2, 0xE4)
GREEN_LINE = RGBColor(0x54, 0x9B, 0x62)
AMBER_FILL = RGBColor(0xFC, 0xE9, 0xD5)
AMBER_LINE = RGBColor(0xC9, 0x7D, 0x2B)
FONT = "Calibri"

# For these DrawingML shapes the sites are top, left, bottom, right.
SITE_TOP, SITE_LEFT, SITE_BOTTOM, SITE_RIGHT = 0, 1, 2, 3


def add_box(shapes, shape_type, left, top, width, height, text,
            fill, line, font_size=20):
    shp = shapes.add_shape(shape_type, Inches(left), Inches(top),
                           Inches(width), Inches(height))
    shp.fill.solid()
    shp.fill.fore_color.rgb = fill
    shp.line.color.rgb = line
    shp.line.width = Pt(1.5)
    tf = shp.text_frame
    tf.word_wrap = False
    tf.vertical_anchor = MSO_ANCHOR.MIDDLE
    para = tf.paragraphs[0]
    para.alignment = PP_ALIGN.CENTER
    run = para.add_run()
    run.text = text
    run.font.size = Pt(font_size)
    run.font.bold = True
    run.font.color.rgb = INK
    run.font.name = FONT
    return shp


def add_arrow(shapes, start_shape, start_site, end_shape, end_site):
    """Straight connector natively glued to both shapes, with a triangle
    tailEnd arrowhead injected via raw XML (python-pptx has no arrowhead API)."""
    conn = shapes.add_connector(MSO_CONNECTOR.STRAIGHT,
                                Inches(0), Inches(0), Inches(1), Inches(1))
    conn.begin_connect(start_shape, start_site)
    conn.end_connect(end_shape, end_site)
    conn.line.color.rgb = LINE
    conn.line.width = Pt(2)
    ln = conn.line._get_or_add_ln()
    tail_end = ln.makeelement(qn("a:tailEnd"), {"type": "triangle"})
    ln.insert_element_before(tail_end, "a:extLst")  # schema order: headEnd, tailEnd, extLst
    return conn


def add_caption(shapes, left, top, width, height, text, font_size=18):
    box = shapes.add_textbox(Inches(left), Inches(top), Inches(width), Inches(height))
    tf = box.text_frame
    tf.word_wrap = False
    tf.vertical_anchor = MSO_ANCHOR.MIDDLE
    para = tf.paragraphs[0]
    para.alignment = PP_ALIGN.CENTER
    run = para.add_run()
    run.text = text
    run.font.size = Pt(font_size)
    run.font.bold = True
    run.font.color.rgb = INK
    run.font.name = FONT
    return box


def build(out_path):
    prs = Presentation()
    slide = prs.slides.add_slide(prs.slide_layouts[6])  # blank layout
    shapes = slide.shapes

    add_caption(shapes, 0.5, 0.45, 9.0, 0.7, "Expense approval", font_size=28)

    request = add_box(shapes, MSO_SHAPE.ROUNDED_RECTANGLE,
                      0.7, 3.3, 1.9, 0.9, "Request", BLUE_FILL, BLUE_LINE)
    approve = add_box(shapes, MSO_SHAPE.DIAMOND,
                      3.5, 2.85, 2.2, 1.8, "Approve?", AMBER_FILL, AMBER_LINE,
                      font_size=18)
    pay = add_box(shapes, MSO_SHAPE.ROUNDED_RECTANGLE,
                  6.9, 1.9, 1.9, 0.9, "Pay", GREEN_FILL, GREEN_LINE)
    revise = add_box(shapes, MSO_SHAPE.ROUNDED_RECTANGLE,
                     6.9, 5.2, 1.9, 0.9, "Revise", AMBER_FILL, AMBER_LINE)

    add_arrow(shapes, request, SITE_RIGHT, approve, SITE_LEFT)
    add_arrow(shapes, approve, SITE_TOP, pay, SITE_LEFT)
    add_arrow(shapes, approve, SITE_BOTTOM, revise, SITE_LEFT)

    add_caption(shapes, 5.55, 2.02, 0.7, 0.4, "Yes")
    add_caption(shapes, 5.55, 4.66, 0.7, 0.4, "No")

    cp = prs.core_properties
    cp.title = "Expense approval flowchart sample"
    cp.subject = "Editable single-slide flowchart sample"
    cp.author = "DocRedock"
    cp.last_modified_by = "DocRedock"
    cp.comments = "Synthetic documentation sample; no real data."
    cp.category = ""
    cp.keywords = ""
    cp.revision = 1
    prs.save(str(out_path))
    return out_path


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("flowchart-sample.pptx")
    out.parent.mkdir(parents=True, exist_ok=True)
    path = build(out)
    print(f"wrote {path} ({path.stat().st_size} bytes, 1 slide)")


if __name__ == "__main__":
    main()
