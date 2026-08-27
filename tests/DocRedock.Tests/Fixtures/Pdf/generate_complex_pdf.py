#!/usr/bin/env python3
"""Generate the deterministic, layout-heavy PDF corpus used by DRMD PDF tests.

Usage:
  python3 generate_complex_pdf.py [output.pdf]
"""
from pathlib import Path
import io
import os
import subprocess
import sys

from PIL import Image, ImageDraw
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import A4, landscape
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (
    BaseDocTemplate, Frame, Image as RLImage, KeepTogether, PageTemplate,
    Paragraph, Spacer, Table, TableStyle, PageBreak,
)

FONT_NAME = "DocRedockFixtureFont"


def resolve_font():
    explicit = os.environ.get("DOCREDOCK_PDF_FONT_PATH")
    if explicit:
        path = Path(explicit).expanduser().resolve()
        if not path.is_file():
            raise FileNotFoundError(f"DOCREDOCK_PDF_FONT_PATH does not exist: {path}")
        face = int(os.environ.get("DOCREDOCK_PDF_FONT_FACE_INDEX", "0"))
        if face < 0:
            raise ValueError("DOCREDOCK_PDF_FONT_FACE_INDEX must be non-negative")
        return path, face

    candidates = [
        Path("/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc"),
        Path("/System/Library/Fonts/ヒラギノ丸ゴ ProN W4.ttc"),
        Path("C:/Windows/Fonts/YuGothR.ttc"),
        Path("C:/Windows/Fonts/meiryo.ttc"),
        Path("/usr/share/fonts/opentype/ipafont-gothic/ipag.ttf"),
        Path("/usr/share/fonts/opentype/ipaexfont-gothic/ipaexg.ttf"),
    ]
    for path in candidates:
        if path.is_file():
            return path, 0

    try:
        match = subprocess.run(
            ["fc-match", "-f", "%{file}\\n", "Noto Sans CJK JP,Noto Sans JP,IPAexGothic,IPAGothic"],
            check=True, capture_output=True, text=True, timeout=5,
        ).stdout.splitlines()
        for value in match:
            path = Path(value.strip())
            if path.is_file():
                return path, 0
    except (FileNotFoundError, subprocess.SubprocessError):
        pass

    raise FileNotFoundError(
        "No installed Japanese font was found. Set DOCREDOCK_PDF_FONT_PATH "
        "and optional DOCREDOCK_PDF_FONT_FACE_INDEX."
    )


FONT, FONT_FACE_INDEX = resolve_font()
pdfmetrics.registerFont(TTFont(FONT_NAME, str(FONT), subfontIndex=FONT_FACE_INDEX))

PAGE_W, PAGE_H = A4
BLUE = colors.HexColor("#17365D")
TEAL = colors.HexColor("#0B7285")
LIGHT_BLUE = colors.HexColor("#EAF2F8")
LIGHT_TEAL = colors.HexColor("#E6F7F8")
INK = colors.HexColor("#1F2937")
MUTED = colors.HexColor("#667085")
RED = colors.HexColor("#B42318")
GREEN = colors.HexColor("#067647")

styles = getSampleStyleSheet()
styles.add(ParagraphStyle("JpTitle", parent=styles["Title"], fontName=FONT_NAME, fontSize=19, leading=24, textColor=BLUE, alignment=TA_LEFT, spaceAfter=4))
styles.add(ParagraphStyle("JpH1", parent=styles["Heading1"], fontName=FONT_NAME, fontSize=13, leading=17, textColor=BLUE, spaceBefore=7, spaceAfter=5))
styles.add(ParagraphStyle("JpH2", parent=styles["Heading2"], fontName=FONT_NAME, fontSize=10.5, leading=14, textColor=TEAL, spaceBefore=5, spaceAfter=3))
styles.add(ParagraphStyle("JpBody", parent=styles["BodyText"], fontName=FONT_NAME, fontSize=8.2, leading=12, textColor=INK, spaceAfter=3))
styles.add(ParagraphStyle("JpSmall", parent=styles["BodyText"], fontName=FONT_NAME, fontSize=6.8, leading=9, textColor=MUTED))
styles.add(ParagraphStyle("JpCell", parent=styles["BodyText"], fontName=FONT_NAME, fontSize=6.7, leading=8.5, textColor=INK))
styles.add(ParagraphStyle("JpCellHead", parent=styles["BodyText"], fontName=FONT_NAME, fontSize=6.8, leading=8.5, textColor=colors.white, alignment=TA_CENTER))
styles.add(ParagraphStyle("JpBullet", parent=styles["BodyText"], fontName=FONT_NAME, fontSize=7.8, leading=11, leftIndent=9, firstLineIndent=-7, textColor=INK, bulletIndent=0))
styles.add(ParagraphStyle("JpCaption", parent=styles["BodyText"], fontName=FONT_NAME, fontSize=6.5, leading=8, textColor=MUTED, alignment=TA_CENTER))

def P(text, style="JpBody"):
    return Paragraph(text, styles[style])

def bullet(text):
    return Paragraph("• " + text, styles["JpBullet"])

def make_chart():
    image = Image.new("RGB", (900, 300), "white")
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 0, 899, 299), outline="#CBD5E1", width=2)
    draw.text((28, 20), "Monthly volume (embedded test image)", fill="#17365D")
    values = [42, 55, 49, 73, 68, 81, 92, 88]
    base_y = 245
    for i, value in enumerate(values):
        x = 55 + i * 100
        top = base_y - value * 2
        draw.rectangle((x, top, x + 48, base_y), fill="#0B7285")
        draw.text((x + 12, base_y + 10), f"M{i+1}", fill="#475467")
        draw.text((x + 13, top - 18), str(value), fill="#17365D")
    buf = io.BytesIO()
    image.save(buf, format="PNG")
    return buf.getvalue()

def header_footer(canvas, doc):
    canvas.saveState()
    canvas.setFont(FONT_NAME, 7)
    canvas.setFillColor(MUTED)
    canvas.drawString(16 * mm, PAGE_H - 11 * mm, "DRMD PDF変換検証 / CONFIDENTIAL")
    canvas.drawRightString(PAGE_W - 16 * mm, 10 * mm, f"ページ {doc.page}")
    canvas.setStrokeColor(colors.HexColor("#D0D5DD"))
    canvas.line(16 * mm, PAGE_H - 14 * mm, PAGE_W - 16 * mm, PAGE_H - 14 * mm)
    canvas.line(16 * mm, 14 * mm, PAGE_W - 16 * mm, 14 * mm)
    canvas.restoreState()

def table(data, widths, header=True):
    t = Table(data, colWidths=widths, repeatRows=1 if header else 0, hAlign="LEFT")
    commands = [
        ("FONTNAME", (0, 0), (-1, -1), FONT_NAME),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("GRID", (0, 0), (-1, -1), 0.35, colors.HexColor("#CBD5E1")),
        ("LEFTPADDING", (0, 0), (-1, -1), 4),
        ("RIGHTPADDING", (0, 0), (-1, -1), 4),
        ("TOPPADDING", (0, 0), (-1, -1), 4),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 4),
    ]
    if header:
        commands += [("BACKGROUND", (0, 0), (-1, 0), BLUE), ("TEXTCOLOR", (0, 0), (-1, 0), colors.white)]
        for row in range(1, len(data)):
            if row % 2 == 0:
                commands.append(("BACKGROUND", (0, row), (-1, row), colors.HexColor("#F8FAFC")))
    t.setStyle(TableStyle(commands))
    return t

def build(path):
    doc = BaseDocTemplate(str(path), pagesize=A4, leftMargin=16*mm, rightMargin=16*mm, topMargin=20*mm, bottomMargin=20*mm, title="DRMD PDF Complex Fixture")
    frame = Frame(doc.leftMargin, doc.bottomMargin, doc.width, doc.height, id="main")
    doc.addPageTemplates([PageTemplate(id="normal", frames=[frame], onPage=header_footer)])
    story = []

    story += [P("経費精算プラットフォーム", "JpTitle"),
              P("PDF → Markdown / DRMD 変換精度検証レポート", "JpH1"),
              P("文書ID: PDF-COMPLEX-001　版: 1.2　作成日: 2026-08-25　作成者: DRMD QA", "JpSmall"),
              Spacer(1, 5)]
    summary = Table([[P("<b>目的</b><br/>複雑な帳票レイアウト、自然な日本語、座標付きテキスト、表・画像を一つのPDFに集約し、変換後の構造と可読性を検証する。", "JpCell"),
                      P("<b>判定基準</b><br/>文字欠落なし / ページ順保持 / 表の行列順保持 / ヘッダー・フッター混入を識別 / 画像参照を保持", "JpCell")]], colWidths=[doc.width/2-4, doc.width/2-4])
    summary.setStyle(TableStyle([("BACKGROUND",(0,0),(-1,-1),LIGHT_BLUE),("BOX",(0,0),(-1,-1),0.6,BLUE),("VALIGN",(0,0),(-1,-1),"TOP"),("LEFTPADDING",(0,0),(-1,-1),8),("RIGHTPADDING",(0,0),(-1,-1),8),("TOPPADDING",(0,0),(-1,-1),7),("BOTTOMPADDING",(0,0),(-1,-1),7)]))
    story += [summary, P("1. 検証観点", "JpH1")]
    left = [P("<b>入力レイアウト</b>", "JpH2"), bullet("A4縦、固定ヘッダー/フッター、ページ番号"), bullet("日本語と英数字・記号・長い識別子の混在"), bullet("表のセル内改行、桁区切り、状態ラベル"), bullet("画像・キャプション・注釈の位置関係")]
    right = [P("<b>変換で守る情報</b>", "JpH2"), bullet("見出しの階層と本文の段落境界"), bullet("複数カラムの読み順（左→右、上→下）"), bullet("表のヘッダー、列数、行順"), bullet("画像はasset参照または説明を伴う")]
    cols = Table([[left, right]], colWidths=[doc.width/2-4, doc.width/2-4])
    cols.setStyle(TableStyle([("BACKGROUND",(0,0),(0,0),LIGHT_TEAL),("BACKGROUND",(1,0),(1,0),colors.HexColor("#F8FAFC")),("BOX",(0,0),(-1,-1),0.4,colors.HexColor("#CBD5E1")),("INNERGRID",(0,0),(-1,-1),0.4,colors.HexColor("#CBD5E1")),("VALIGN",(0,0),(-1,-1),"TOP"),("LEFTPADDING",(0,0),(-1,-1),8),("RIGHTPADDING",(0,0),(-1,-1),8),("TOPPADDING",(0,0),(-1,-1),5),("BOTTOMPADDING",(0,0),(-1,-1),5)]))
    story += [cols, P("2. システム構成とデータフロー", "JpH1")]
    flow = [
        [P("入力", "JpCellHead"), P("抽出", "JpCellHead"), P("正規化", "JpCellHead"), P("出力", "JpCellHead")],
        [P("PDF-COMPLEX-001<br/>日本語帳票 / 3ページ", "JpCell"), P("PdfTextExtractor<br/>座標・フォント・ActualText", "JpCell"), P("DocumentGraph<br/>page partition / paragraph nodes", "JpCell"), P("report.md<br/>report.drmd + sidecar", "JpCell")],
    ]
    story += [table(flow, [doc.width/4]*4), P("処理上の注意: 同一ページ上のヘッダー・本文・脚注は座標のY値だけでは分離しにくい。変換結果ではページ境界コメントと原文の順序を照合する。", "JpSmall"),
              P("3. 月次KPIと承認状態", "JpH1")]
    kpi = [[P(x, "JpCellHead") for x in ["指標ID", "指標名", "目標値", "実績", "判定", "備考"]]]
    rows = [
        ["KPI-01", "申請処理時間 p95", "1.0秒以下", "0.82秒", "合格", "添付転送時間を除外"],
        ["KPI-02", "月間申請件数", "50,000件", "42,680件", "合格", "2026年7月集計"],
        ["KPI-03", "差戻し率", "8%未満", "9.4%", "要確認", "部門別の偏りを調査"],
        ["KPI-04", "監査ログ欠落", "0件", "0件", "合格", "日次検査・改ざん検知"],
        ["KPI-05", "支払連携遅延", "5分未満", "3分12秒", "合格", "再送5回まで"],
    ]
    for row in rows: kpi.append([P(x, "JpCell") for x in row])
    story += [table(kpi, [24*mm, 37*mm, 28*mm, 24*mm, 21*mm, doc.width-134*mm]), Spacer(1, 4)]
    story += [P("承認フロー", "JpH2")]
    approval = [[P(x, "JpCellHead") for x in ["状態", "担当", "遷移条件", "SLA", "エラー時の扱い"]]]
    for row in [
        ["DRAFT", "申請者", "入力保存", "なし", "下書きとして保持"],
        ["SUBMITTED", "申請者", "提出ボタン / 必須項目OK", "即時", "ProblemDetailsを返す"],
        ["PENDING", "承認者", "承認キューへ登録", "24時間", "期限超過アラート"],
        ["RETURNED", "承認者", "コメント付き差戻し", "48時間", "申請者へ通知"],
        ["APPROVED", "承認者", "全承認条件OK", "24時間", "支払連携へ進む"],
        ["ERROR", "運用担当", "retry_count >= 5", "30分", "DLQへ隔離・手動再送"],
    ]: approval.append([P(x, "JpCell") for x in row])
    story += [table(approval, [25*mm, 25*mm, 58*mm, 25*mm, doc.width-133*mm]), PageBreak()]

    story += [P("4. 左右カラムの業務仕様", "JpH1")]
    left_body = [P("4.1 申請者向け", "JpH2"), bullet("領収書はPDF/JPEG/PNG、1ファイル10MB、合計50MBまで。"), bullet("金額は税込・税抜を明示し、通貨コードはISO 4217を使用。"), bullet("同一Idempotency-Keyの再送は同じclaim_idを返す。"), P("4.2 API境界", "JpH2"), bullet("POST /v1/expense-claims は201 CreatedまたはRFC 7807形式のエラーを返す。"), bullet("Authorization: Bearer JWT、TLS 1.2以上、監査IDを必須化。")]
    right_body = [P("4.3 承認者向け", "JpH2"), bullet("承認・差戻しには理由を記録し、前後状態を監査ログへ追記する。"), bullet("金額が100,000円を超える場合は二段階承認へ分岐。"), bullet("代理承認は有効期限と委任元を明記する。"), P("4.4 運用・障害対応", "JpH2"), bullet("再送間隔は1/5/30/120/600秒、最大5回。"), bullet("DLQ滞留が100件を超えた場合は重大アラートを発報する。")]
    columns2 = Table([[left_body, right_body]], colWidths=[doc.width/2-4, doc.width/2-4])
    columns2.setStyle(TableStyle([("VALIGN",(0,0),(-1,-1),"TOP"),("BOX",(0,0),(-1,-1),0.5,colors.HexColor("#CBD5E1")),("INNERGRID",(0,0),(-1,-1),0.5,colors.HexColor("#CBD5E1")),("LEFTPADDING",(0,0),(-1,-1),8),("RIGHTPADDING",(0,0),(-1,-1),8),("TOPPADDING",(0,0),(-1,-1),6),("BOTTOMPADDING",(0,0),(-1,-1),6)]))
    story += [columns2, P("5. 画像・注釈・測定結果", "JpH1")]
    chart = RLImage(io.BytesIO(make_chart()), width=doc.width, height=doc.width*300/900)
    story += [chart, P("図1. 月別処理量の測定値。画像はPDF内に埋め込まれたPNGであり、変換後は画像assetとして参照可能であること。", "JpCaption")]
    notes = [[P("<b>注記A</b>", "JpCell"), P("画像の文字は本文テキストではなく、OCRを有効化した場合のみ派生テキストになる。", "JpCell")],
             [P("<b>注記B</b>", "JpCell"), P("ページ下部の脚注は本文と混ぜず、位置情報を保って確認する。", "JpCell")],
             [P("<b>注記C</b>", "JpCell"), P("マーカー PDF-COMPLEX-001 / END を抽出結果の完全性確認に使う。", "JpCell")]]
    nt = Table(notes, colWidths=[27*mm, doc.width-27*mm])
    nt.setStyle(TableStyle([("BACKGROUND",(0,0),(0,-1),LIGHT_BLUE),("GRID",(0,0),(-1,-1),0.35,colors.HexColor("#CBD5E1")),("VALIGN",(0,0),(-1,-1),"TOP"),("LEFTPADDING",(0,0),(-1,-1),5),("RIGHTPADDING",(0,0),(-1,-1),5),("TOPPADDING",(0,0),(-1,-1),5),("BOTTOMPADDING",(0,0),(-1,-1),5)]))
    story += [nt, PageBreak()]

    story += [P("6. 付録: 変換検証チェックリスト", "JpH1")]
    checklist = [[P(x, "JpCellHead") for x in ["No.", "確認項目", "期待結果", "結果"]]]
    for i, row in enumerate([
        ("01", "文書IDと版数", "PDF-COMPLEX-001 / 1.2 が保持される", "PASS"),
        ("02", "日本語と英数字", "文字化け・欠落がない", "PASS"),
        ("03", "表の列数", "6列KPI / 5列承認表を保持", "PASS"),
        ("04", "複数カラム", "左カラム→右カラムの順で読める", "REVIEW"),
        ("05", "画像参照", "図1のassetまたは注釈が残る", "REVIEW"),
        ("06", "ヘッダー/フッター", "ページ番号が本文ノードを汚染しない", "REVIEW"),
        ("07", "終端マーカー", "PDF-COMPLEX-001 / END が存在", "PASS"),
    ]):
        checklist.append([P(x, "JpCell") for x in row])
    ct = table(checklist, [14*mm, 43*mm, doc.width-92*mm, 25*mm])
    ct.setStyle(TableStyle([("TEXTCOLOR",(3,1),(3,-1),GREEN),("BACKGROUND",(3,4),(3,6),colors.HexColor("#FFF4E5"))]))
    story += [ct, Spacer(1, 8), P("再現情報", "JpH2"), P(f"このPDFは tests/DocRedock.Tests/Fixtures/Pdf/generate_complex_pdf.py から生成する。フォントはシステムまたは DOCREDOCK_PDF_FONT_PATH から選択し、日時・識別子・測定値は固定している（{FONT.name}）。", "JpBody"), Spacer(1, 20), P("PDF-COMPLEX-001 / END", "JpTitle")]
    doc.build(story)

if __name__ == "__main__":
    destination = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "tests/DocRedock.Tests/Fixtures/Pdf/complex-layout.pdf"
    destination.parent.mkdir(parents=True, exist_ok=True)
    build(destination)
    print(destination)
