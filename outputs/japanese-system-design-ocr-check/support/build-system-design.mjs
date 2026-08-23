import fs from "node:fs/promises";
import path from "node:path";
import { SpreadsheetFile, Workbook } from "@oai/artifact-tool";

const root = "/Users/takayuki/git/RTMD/outputs/japanese-system-design-ocr-check";
const support = path.join(root, "support");
const workbookPath = path.join(root, "japanese-system-design-ocr-sample.xlsx");
const previewDir = path.join(support, "previews");

const navy = "#173A59";
const blue = "#2F75B5";
const paleBlue = "#DCEAF7";
const paleGreen = "#E2F0D9";
const paleYellow = "#FFF2CC";
const paleRed = "#FCE4D6";
const gray = "#F2F2F2";
const grid = "#D9E2F3";
const bodyFont = { name: "Yu Gothic", size: 10, color: "#1F2937" };

const workbook = Workbook.create();
const overview = workbook.worksheets.add("設計概要");
const sequence = workbook.worksheets.add("シーケンス図");
const flow = workbook.worksheets.add("業務フロー");
const ocrExpected = workbook.worksheets.add("OCR期待値");
const screen = workbook.worksheets.add("画面設計");

function baseSheet(sheet, usedRange, widthPx = 26, rowHeightPx = 20) {
  sheet.showGridLines = false;
  const range = sheet.getRange(usedRange);
  range.format = {
    font: bodyFont,
    verticalAlignment: "center",
    wrapText: true,
  };
  range.format.columnWidthPx = widthPx;
  range.format.rowHeightPx = rowHeightPx;
}

function mergeWrite(sheet, address, value, format = {}) {
  const range = sheet.getRange(address);
  range.merge();
  sheet.getRange(address.split(":")[0]).values = [[value]];
  range.format = {
    font: bodyFont,
    verticalAlignment: "center",
    wrapText: true,
    ...format,
  };
  return range;
}

function title(sheet, address, text, subtitle) {
  const titleRange = mergeWrite(sheet, address, text, {
    fill: navy,
    font: { name: "Yu Gothic", size: 17, bold: true, color: "#FFFFFF" },
    horizontalAlignment: "left",
  });
  titleRange.format.rowHeightPx = 31;
  if (subtitle) {
    const start = address.split(":")[0].replace(/[0-9]+$/, "3");
    const end = address.split(":")[1].replace(/[0-9]+$/, "3");
    mergeWrite(sheet, `${start}:${end}`, subtitle, {
      fill: "#EAF1F7",
      font: { name: "Yu Gothic", size: 9, color: "#4B657A" },
      horizontalAlignment: "left",
    });
  }
}

// 1. 設計概要・数式検証
baseSheet(overview, "A1:N22", 52, 22);
overview.getRange("A1:A22").format.columnWidthPx = 130;
overview.getRange("B1:C22").format.columnWidthPx = 78;
overview.getRange("D1:D22").format.columnWidthPx = 76;
overview.getRange("E1:F22").format.columnWidthPx = 80;
overview.getRange("G1:G22").format.columnWidthPx = 96;
overview.getRange("H1:H22").format.columnWidthPx = 70;
overview.getRange("I1:I22").format.columnWidthPx = 112;
overview.getRange("J1:J22").format.columnWidthPx = 16;
overview.getRange("K1:K22").format.columnWidthPx = 90;
overview.getRange("L1:L22").format.columnWidthPx = 76;
overview.getRange("M1:N22").format.columnWidthPx = 24;
overview.freezePanes.freezeRows(4);
title(overview, "A1:N2", "受注管理システム　基本設計書", "Excel方眼紙・シーケンス・業務フロー・貼付画面・OCRを含むRTMD変換確認用サンプル");
overview.getRange("A5:N5").values = [["文書ID", "DOC-ORD-BD-001", null, null, "版数", "1.2", null, "作成日", new Date("2026-08-22T00:00:00+09:00"), null, "作成者", "開発1課", null, null]];
overview.getRange("A5:N5").format = { fill: gray, font: { name: "Yu Gothic", size: 9, bold: true, color: navy }, borders: { preset: "all", style: "thin", color: "#CBD5E1" } };
overview.getRange("I5").format.numberFormat = "yyyy-mm-dd";

mergeWrite(overview, "A7:N7", "設計・試験進捗（入力値は青、計算セルは緑）", {
  fill: paleBlue,
  font: { name: "Yu Gothic", size: 11, bold: true, color: navy },
});
overview.getRange("A8:G8").values = [["成果物", "予定件数", "完了件数", "完了率", "工数(h)", "単価(円)", "金額(円)"]];
overview.getRange("A9:C11").values = [
  ["API基本設計", 12, 12],
  ["画面基本設計", 8, 6],
  ["結合試験項目", 10, 7],
];
overview.getRange("E9:F11").values = [[24, 7500], [18, 7500], [30, 6500]];
overview.getRange("D9").formulas = [["=C9/B9"]];
overview.getRange("D9:D11").fillDown();
overview.getRange("G9").formulas = [["=E9*F9"]];
overview.getRange("G9:G11").fillDown();
overview.getRange("A12").values = [["合計 / 総合"]];
overview.getRange("B12").formulas = [["=SUM(B9:B11)"]];
overview.getRange("C12").formulas = [["=SUM(C9:C11)"]];
overview.getRange("D12").formulas = [["=C12/B12"]];
overview.getRange("E12").formulas = [["=SUM(E9:E11)"]];
overview.getRange("G12").formulas = [["=SUM(G9:G11)"]];
overview.getRange("A8:G12").format.borders = { preset: "all", style: "thin", color: "#B8C6D1" };
overview.getRange("A8:G8").format = { fill: navy, font: { name: "Yu Gothic", size: 9, bold: true, color: "#FFFFFF" }, horizontalAlignment: "center", borders: { preset: "all", style: "thin", color: "#8094A6" } };
overview.getRange("B9:C11").format = { fill: "#EAF2F8", font: { name: "Yu Gothic", size: 10, color: "#0000FF" }, numberFormat: "#,##0", horizontalAlignment: "right" };
overview.getRange("E9:F11").format = { fill: "#EAF2F8", font: { name: "Yu Gothic", size: 10, color: "#0000FF" }, numberFormat: "#,##0", horizontalAlignment: "right" };
overview.getRange("D9:D12").format = { fill: paleGreen, font: { name: "Yu Gothic", size: 10, color: "#008000" }, numberFormat: "0.0%", horizontalAlignment: "right" };
overview.getRange("G9:G12").format = { fill: paleGreen, font: { name: "Yu Gothic", size: 10, color: "#008000" }, numberFormat: "#,##0", horizontalAlignment: "right" };
overview.getRange("B12:C12").format = { fill: paleGreen, font: { name: "Yu Gothic", size: 10, color: "#008000" }, numberFormat: "#,##0", horizontalAlignment: "right" };
overview.getRange("E12").format = { fill: paleGreen, font: { name: "Yu Gothic", size: 10, color: "#008000" }, numberFormat: "#,##0", horizontalAlignment: "right" };
overview.getRange("A12:G12").format.borders = { preset: "doubleBottom", style: "double", color: navy };

mergeWrite(overview, "I8:N8", "RTMD確認ポイント", { fill: paleYellow, font: { name: "Yu Gothic", size: 11, bold: true, color: "#7A5200" } });
overview.getRange("I9:J14").values = [
  ["数式セル数", null],
  ["総予定件数", null],
  ["総完了件数", null],
  ["総合完了率", null],
  ["総金額", null],
  ["OCR期待行数", null],
];
overview.getRange("K9").formulas = [["=COUNTA(D9:D12)+COUNTA(G9:G12)+COUNTA(B12:C12)+COUNTA(E12)"]];
overview.getRange("K10").formulas = [["=B12"]];
overview.getRange("K11").formulas = [["=C12"]];
overview.getRange("K12").formulas = [["=D12"]];
overview.getRange("K13").formulas = [["=G12"]];
overview.getRange("K14").formulas = [["=COUNTA('OCR期待値'!B5:B29)"]];
overview.getRange("I9:K14").format.borders = { preset: "all", style: "thin", color: "#C9B458" };
overview.getRange("I9:J14").format.fill = "#FFF9E6";
overview.getRange("K9:K14").format = { fill: paleGreen, font: { name: "Yu Gothic", size: 10, color: "#008000" }, horizontalAlignment: "right" };
overview.getRange("K12").format.numberFormat = "0.0%";
overview.getRange("K13").format.numberFormat = "#,##0";
mergeWrite(overview, "A16:N18", "凡例：青字＝入力値／緑字＝数式。RTMDのMarkdownでは数式セルを `=式` → 計算結果 の形式で投影し、計算結果側は参照専用です。", {
  fill: "#F8FAFC",
  font: { name: "Yu Gothic", size: 10, color: "#475569" },
  borders: { preset: "outside", style: "thin", color: "#CBD5E1" },
  horizontalAlignment: "left",
});

// 2. Excel方眼紙シーケンス
baseSheet(sequence, "A1:Y34", 29, 20);
sequence.freezePanes.freezeRows(3);
title(sequence, "A1:Y2", "注文詳細表示・確定　シーケンス図", "セル結合・細幅列・罫線・矢印文字で作成した、典型的なExcel方眼紙形式");
const actors = [
  ["B4:F6", "利用者\n（営業担当）", paleBlue],
  ["H4:L6", "Webブラウザ\nORD-DTL-01", "#E2F0D9"],
  ["N4:R6", "注文API\nOrderService", paleYellow],
  ["T4:X6", "注文DB\nORDERS / ITEMS", paleRed],
];
for (const [address, label, fill] of actors) {
  mergeWrite(sequence, address, label, {
    fill,
    font: { name: "Yu Gothic", size: 10, bold: true, color: navy },
    borders: { preset: "all", style: "medium", color: navy },
    horizontalAlignment: "center",
  });
}
for (const column of ["D", "J", "P", "V"]) {
  sequence.getRange(`${column}7:${column}33`).values = Array.from({ length: 27 }, () => ["┆"]);
  sequence.getRange(`${column}7:${column}33`).format = { font: { name: "Yu Gothic", size: 10, color: "#8193A4" }, horizontalAlignment: "center" };
}
const messages = [
  ["E8:I9", "1. 注文詳細を開く　────────▶", paleBlue],
  ["K11:O12", "2. GET /api/orders/{id}　────────▶", paleBlue],
  ["Q14:U15", "3. 注文・明細を検索　────────▶", paleYellow],
  ["Q17:U18", "4. 注文データ　◀────────", paleGreen],
  ["K20:O21", "5. 200 OK (JSON)　◀────────", paleGreen],
  ["E23:I24", "6. 注文詳細を表示　◀────────", paleGreen],
  ["K26:O27", "7. POST /confirm　────────▶", paleBlue],
  ["Q29:U30", "8. status = CONFIRMED　────────▶", paleYellow],
];
for (const [address, label, fill] of messages) {
  mergeWrite(sequence, address, label, {
    fill,
    font: { name: "Yu Gothic", size: 9, color: "#20384F" },
    borders: { bottom: { style: "medium", color: blue } },
    horizontalAlignment: "center",
  });
}
mergeWrite(sequence, "N32:X33", "代替：在庫不足時は HTTP 409 / OUT_OF_STOCK を返し、確定処理を中断する", {
  fill: "#FFF4F0",
  font: { name: "Yu Gothic", size: 9, italic: true, color: "#9C3D2E" },
  borders: { preset: "outside", style: "dashed", color: "#C55A11" },
  horizontalAlignment: "left",
});

// 3. Excel方眼紙フロー
baseSheet(flow, "A1:R33", 33, 21);
flow.freezePanes.freezeRows(4);
title(flow, "A1:R2", "注文確定　業務フロー", "部門別レーンと結合セルで再現したフローチャート");
const lanes = [["A4:F4", "利用者"], ["G4:L4", "Web画面"], ["M4:R4", "API / DB"]];
for (const [address, label] of lanes) mergeWrite(flow, address, label, { fill: navy, font: { name: "Yu Gothic", size: 10, bold: true, color: "#FFFFFF" }, horizontalAlignment: "center" });
flow.getRange("A5:R33").format.borders = { preset: "all", style: "thin", color: "#E5E7EB" };
const boxes = [
  ["B6:E8", "開始\n注文詳細を確認", paleBlue],
  ["B11:E13", "［注文を確定］\nボタン押下", paleBlue],
  ["H11:K13", "入力内容を\nクライアント検証", "#E2F0D9"],
  ["H16:K19", "◇ 入力値は\n妥当か？ ◇", paleYellow],
  ["H22:K24", "エラーを表示\n入力欄へ戻る", paleRed],
  ["N16:Q19", "注文・在庫を\n再検証", paleBlue],
  ["N22:Q25", "◇ 在庫は\n確保可能か？ ◇", paleYellow],
  ["N28:Q30", "注文確定・\n出荷指示を登録", paleGreen],
  ["B28:E30", "完了メッセージを\n確認", paleGreen],
  ["B32:E33", "終了", gray],
];
for (const [address, label, fill] of boxes) mergeWrite(flow, address, label, {
  fill,
  font: { name: "Yu Gothic", size: 10, bold: true, color: navy },
  borders: { preset: "all", style: "medium", color: blue },
  horizontalAlignment: "center",
});
const arrows = [
  ["C9:D10", "↓"], ["F12:G12", "→"], ["I14:J15", "↓"], ["L17:M17", "はい →"],
  ["I20:J21", "いいえ ↓"], ["L23:M23", "← エラー"], ["O20:P21", "↓"], ["O26:P27", "はい ↓"],
  ["L29:M29", "← 完了"], ["C31:D31", "↓"],
];
for (const [address, label] of arrows) mergeWrite(flow, address, label, { font: { name: "Yu Gothic", size: 11, bold: true, color: blue }, horizontalAlignment: "center" });
mergeWrite(flow, "M26:R27", "いいえ：在庫不足エラーを返却", { fill: "#FFF4F0", font: { name: "Yu Gothic", size: 9, color: "#9C3D2E" }, horizontalAlignment: "center" });

// 4. OCR正解データ
baseSheet(ocrExpected, "A1:H38", 68, 22);
ocrExpected.getRange("A1:A38").format.columnWidthPx = 48;
ocrExpected.getRange("B1:B38").format.columnWidthPx = 510;
ocrExpected.getRange("C1:C38").format.columnWidthPx = 180;
ocrExpected.getRange("D1:D38").format.columnWidthPx = 24;
ocrExpected.getRange("E1:E38").format.columnWidthPx = 100;
ocrExpected.getRange("F1:F38").format.columnWidthPx = 125;
ocrExpected.getRange("G1:H38").format.columnWidthPx = 30;
ocrExpected.getRange("A5:C29").format.rowHeightPx = 28;
ocrExpected.freezePanes.freezeRows(5);
title(ocrExpected, "A1:H2", "OCR評価用 正解データ", "画面設計シートに貼り付けたスクリーンショット内の主要文字列。OCR結果との文字誤り率（CER）算定に使用する");
ocrExpected.getRange("A4:C4").values = [["No.", "期待文字列", "難易度・観点"]];
ocrExpected.getRange("A4:C4").format = { fill: navy, font: { name: "Yu Gothic", size: 9, bold: true, color: "#FFFFFF" }, borders: { preset: "all", style: "thin", color: "#8094A6" } };
const expectedLines = [
  [1, "注文管理システム", "大見出し"],
  [2, "営業部 山田 太郎 ログアウト", "空白・区切り"],
  [3, "ダッシュボード", "白抜き文字"],
  [4, "注文検索", "白抜き・太字"],
  [5, "在庫照会", "白抜き文字"],
  [6, "出荷管理", "白抜き文字"],
  [7, "マスタ管理", "白抜き文字"],
  [8, "注文詳細", "見出し"],
  [9, "基本情報", "見出し"],
  [10, "出荷準備中", "小型バッジ"],
  [11, "注文番号 ORD-2026-00128", "英数字・ハイフン"],
  [12, "注文日 2026年8月22日", "和文日付"],
  [13, "顧客名 株式会社サンプル商事", "漢字・カナ"],
  [14, "担当者 佐藤 花子", "人名・空白"],
  [15, "配送先 東京都千代田区丸の内1-2-3", "住所・数字"],
  [16, "支払方法 請求書払い（月末締め）", "括弧"],
  [17, "注文明細", "見出し"],
  [18, "商品コード 商品名 数量 単価 金額", "表ヘッダー"],
  [19, "PC-AX104 業務用ノートPC 14型 2 48,000円 96,000円", "英数字・金額"],
  [20, "AC-DK210 USB-Cドッキングステーション 2 12,000円 24,000円", "長いカナ"],
  [21, "SV-SET01 初期設定サービス 2 4,200円 8,400円", "英数字・金額"],
  [22, "合計金額（税込） 128,400円", "強調金額"],
  [23, "キャンセル", "ボタン"],
  [24, "注文を確定", "白抜きボタン"],
  [25, "最終更新: 2026/08/22 14:35 画面ID: ORD-DTL-01", "小さい低コントラスト文字"],
];
ocrExpected.getRange(`A5:C${4 + expectedLines.length}`).values = expectedLines;
ocrExpected.getRange(`A4:C${4 + expectedLines.length}`).format.borders = { preset: "all", style: "thin", color: "#CBD5E1" };
ocrExpected.getRange(`A5:A${4 + expectedLines.length}`).format = { horizontalAlignment: "right", numberFormat: "0" };
ocrExpected.getRange(`B5:B${4 + expectedLines.length}`).format = { font: { name: "Yu Gothic", size: 10, color: "#1F2937" }, wrapText: true };
ocrExpected.getRange(`C5:C${4 + expectedLines.length}`).format = { fill: "#F8FAFC", font: { name: "Yu Gothic", size: 9, color: "#64748B" } };
ocrExpected.getRange("E5:F8").values = [["集計", "値"], ["期待行数", null], ["評価対象", "主要25行"], ["評価指標", "文字誤り率(CER)"]];
ocrExpected.getRange("F6").formulas = [["=COUNTA(B5:B29)"]];
ocrExpected.getRange("E5:F8").format.borders = { preset: "all", style: "thin", color: "#B8C6D1" };
ocrExpected.getRange("E5:F5").format = { fill: paleYellow, font: { name: "Yu Gothic", size: 9, bold: true, color: "#7A5200" } };
ocrExpected.getRange("F6").format = { fill: paleGreen, font: { name: "Yu Gothic", size: 10, color: "#008000" }, horizontalAlignment: "right" };

// 5. 貼付スクリーンショットを含む画面設計
baseSheet(screen, "A1:Z42", 31, 20);
screen.freezePanes.freezeRows(5);
title(screen, "A1:Z2", "画面基本設計　注文詳細（ORD-DTL-01）", "Excelセル方眼紙の上に画面キャプチャを貼り付けた実務風レイアウト。貼付画像はRTMDのアセット抽出・OCR対象");
screen.getRange("A4:Z4").values = [["画面ID", "ORD-DTL-01", null, null, "機能名", "注文詳細表示・確定", null, null, null, "権限", "営業担当", null, null, "更新方式", "同期", null, "備考", "在庫再検証あり", null, null, null, null, null, null, null, null]];
screen.getRange("A4:Z4").format = { fill: gray, font: { name: "Yu Gothic", size: 8, bold: true, color: navy }, borders: { preset: "all", style: "thin", color: "#CBD5E1" } };
mergeWrite(screen, "A5:Z5", "貼付画面（OCR対象）", { fill: paleBlue, font: { name: "Yu Gothic", size: 10, bold: true, color: navy }, horizontalAlignment: "left" });
const screenBytes = await fs.readFile(path.join(support, "order-screen.png"));
screen.images.add({
  dataUrl: `data:image/png;base64,${screenBytes.toString("base64")}`,
  anchor: { from: { row: 5, col: 1 }, extent: { widthPx: 900, heightPx: 570 } },
});
mergeWrite(screen, "A36:Z36", "OCR評価ポイント", { fill: paleYellow, font: { name: "Yu Gothic", size: 10, bold: true, color: "#7A5200" }, horizontalAlignment: "left" });
mergeWrite(screen, "A37:Z39", "日本語見出し／白抜きナビ／英数字ID／カナ長文／住所／金額／括弧／小さい低コントラスト文字を含む。正解文字列は「OCR期待値」シートを参照。", {
  fill: "#FFF9E6",
  font: { name: "Yu Gothic", size: 9, color: "#6B5500" },
  borders: { preset: "outside", style: "thin", color: "#D6B656" },
  horizontalAlignment: "left",
});
screen.getRange("A41:Z42").values = [["設計メモ", "確定ボタン押下時は注文・在庫を再検証し、競合時は409を表示する。", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null], ["OCR対象画像", "xl/media/image1.png", null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null]];
screen.getRange("A41:Z42").format = { fill: "#F8FAFC", font: { name: "Yu Gothic", size: 8, color: "#475569" }, borders: { preset: "all", style: "thin", color: "#E2E8F0" } };

await fs.mkdir(previewDir, { recursive: true });

const checks = [];
for (const [sheetName, range] of [
  ["設計概要", "A1:N18"],
  ["シーケンス図", "A1:Y34"],
  ["業務フロー", "A1:R33"],
  ["OCR期待値", "A1:H30"],
  ["画面設計", "A1:Z42"],
]) {
  const rendered = await workbook.render({ sheetName, range, scale: 1, format: "png" });
  await fs.writeFile(path.join(previewDir, `${sheetName}.png`), new Uint8Array(await rendered.arrayBuffer()));
  const inspection = await workbook.inspect({ kind: "table", sheetId: sheetName, range, include: "values,formulas", tableMaxRows: 18, tableMaxCols: 14, maxChars: 5000 });
  checks.push({ sheetName, inspection: inspection.ndjson });
}

const formulaErrors = await workbook.inspect({
  kind: "match",
  searchTerm: "#REF!|#DIV/0!|#VALUE!|#NAME\\?|#N/A",
  options: { useRegex: true, maxResults: 300 },
  summary: "final formula error scan",
  maxChars: 5000,
});

const output = await SpreadsheetFile.exportXlsx(workbook);
await output.save(workbookPath);

console.log(JSON.stringify({ workbookPath, checks, formulaErrors: formulaErrors.ndjson }, null, 2));
