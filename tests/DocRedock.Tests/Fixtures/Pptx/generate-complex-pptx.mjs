import { Presentation, PresentationFile } from "@oai/artifact-tool";

const outputPath = process.argv[2];
if (!outputPath) throw new Error("Usage: generate-complex-pptx.mjs <output.pptx>");

const deck = Presentation.create({ slideSize: { width: 1280, height: 720 } });
const palette = { ink: "#111827", muted: "#475569", panel: "#F1F5F9", rule: "#CBD5E1", accent: "#2563EB", accentSoft: "#DBEAFE", green: "#047857" };

function addText(slide, name, text, position, style = {}) {
  const shape = slide.shapes.add({ geometry: "textbox", name, position, fill: "none", line: { style: "solid", fill: "none", width: 0 } });
  shape.text = text;
  shape.text.style = { color: palette.ink, fontSize: 20, typeface: "Aptos", verticalAlignment: "top", ...style };
  return shape;
}

function addTitle(slide, text, subtitle) {
  addText(slide, "fixture-title", text, { left: 72, top: 48, width: 960, height: 58 }, { fontSize: 40, bold: true });
  addText(slide, "fixture-subtitle", subtitle, { left: 72, top: 112, width: 980, height: 32 }, { color: palette.muted, fontSize: 18 });
  slide.shapes.add({ geometry: "line", name: "title-rule", position: { left: 72, top: 154, width: 1136, height: 0 }, fill: "none", line: { style: "solid", fill: palette.rule, width: 1 } });
}

function addFooter(slide, page) {
  addText(slide, "footer", "DRMD PPTX complex fixture · " + page, { left: 72, top: 682, width: 1136, height: 20 }, { color: palette.muted, fontSize: 12, alignment: "right" });
}

function addLabeledBox(slide, name, title, body, position, fill = "white") {
  const box = slide.shapes.add({ geometry: "roundRect", name, position, fill, line: { style: "solid", fill: palette.rule, width: 1 }, borderRadius: "rounded-xl" });
  box.text.set([
    [{ run: title, textStyle: { bold: true, color: palette.ink, fontSize: "18pt" } }],
    [{ run: body, textStyle: { color: palette.muted, fontSize: "13pt" } }],
  ]);
  box.text.style = { fontSize: 18, color: palette.ink, insets: { top: 18, right: 18, bottom: 12, left: 18 } };
  return box;
}

{
  const slide = deck.slides.add();
  slide.background.fill = "white";
  addText(slide, "fixture-title", "Project Atlas: conversion acceptance", { left: 72, top: 62, width: 1030, height: 74 }, { fontSize: 50, bold: true });
  addText(slide, "fixture-subtitle", "A deliberately complex PowerPoint corpus for Markdown and DRMD round-trip validation", { left: 76, top: 150, width: 920, height: 34 }, { color: palette.muted, fontSize: 20 });
  const callout = slide.shapes.add({ geometry: "roundRect", name: "acceptance-callout", position: { left: 72, top: 238, width: 1136, height: 302 }, fill: palette.panel, line: { style: "solid", fill: palette.rule, width: 1 }, borderRadius: "rounded-2xl" });
  callout.text.set([
    [{ run: "Acceptance focus", textStyle: { bold: true, color: palette.accent, fontSize: "20pt" } }],
    { bulletCharacter: "•", marginLeft: 28, indent: -14, runs: [{ run: "Preserve ", textStyle: { color: palette.ink, fontSize: "18pt" } }, { run: "日本語・English", textStyle: { bold: true, color: palette.ink, fontSize: "18pt" } }, " text, emphasis, and list structure."] },
    { bulletCharacter: "•", marginLeft: 28, indent: -14, runs: [{ run: "Keep ", textStyle: { color: palette.ink, fontSize: "18pt" } }, { run: "native tables, charts, pictures, and connectors", textStyle: { bold: true, color: palette.ink, fontSize: "18pt" } }, " protected during F1 edits."] },
    { bulletCharacter: "•", marginLeft: 28, indent: -14, runs: [{ run: "Retain presenter notes with the [Sources] marker.", textStyle: { color: palette.ink, fontSize: "18pt" } }] },
  ]);
  callout.text.style = { fontSize: 21, color: palette.ink, insets: { top: 28, right: 34, bottom: 24, left: 34 } };
  addFooter(slide, "01");
  slide.speakerNotes.textFrame.setText(["[Sources]", "This deck is a synthetic regression corpus; no external claims or assets are used.", "Presenter cue: confirm the first F1 edit changes only editable title text."]);
  slide.speakerNotes.setVisible(true);
}

{
  const slide = deck.slides.add();
  slide.background.fill = "white";
  addTitle(slide, "The release flow keeps ownership visible", "Connected native shapes exercise protected connector extraction.");
  const intake = addLabeledBox(slide, "intake", "01 · Intake", "Read original.pptx and build a graph.", { left: 72, top: 258, width: 230, height: 170 }, palette.accentSoft);
  const project = addLabeledBox(slide, "projection", "02 · Projection", "Export editable text as Markdown.", { left: 374, top: 258, width: 230, height: 170 });
  const review = addLabeledBox(slide, "review", "03 · Review", "Inspect changes and diagnostics.", { left: 676, top: 258, width: 230, height: 170 });
  const restore = addLabeledBox(slide, "restore", "04 · Restore", "Patch existing editable shapes only.", { left: 978, top: 258, width: 230, height: 170 }, "#ECFDF5");
  slide.shapes.connect(intake, project, { kind: "straight", fromSide: "right", toSide: "left", line: { style: "solid", fill: palette.accent, width: 3 }, tail: { type: "arrow", width: "med", length: "med" } });
  slide.shapes.connect(project, review, { kind: "straight", fromSide: "right", toSide: "left", line: { style: "solid", fill: palette.accent, width: 3 }, tail: { type: "arrow", width: "med", length: "med" } });
  slide.shapes.connect(review, restore, { kind: "straight", fromSide: "right", toSide: "left", line: { style: "solid", fill: palette.green, width: 3 }, tail: { type: "arrow", width: "med", length: "med" } });
  addText(slide, "flow-caption", "Expected F1 boundary: text in named shapes may change; connector geometry remains protected.", { left: 182, top: 492, width: 916, height: 30 }, { color: palette.muted, fontSize: 16, alignment: "center" });
  addFooter(slide, "02");
  slide.speakerNotes.textFrame.setText(["[Sources]", "Synthetic workflow diagram for PPTX round-trip coverage."]);
  slide.speakerNotes.setVisible(true);
}

{
  const slide = deck.slides.add();
  slide.background.fill = "white";
  addTitle(slide, "Native data objects remain visible and protected", "A merged table and clustered chart validate extraction without flattening the slide.");
  const table = slide.tables.add({ rows: 5, columns: 4, left: 72, top: 206, width: 570, height: 330, columnWidths: [150, 140, 130, 150], values: [
    ["Workstream", "Owner", "Status", "Target"], ["Extraction", "Platform", "Ready", "2026-09-04"], ["Markdown", "Docs", "Review", "2026-09-08"], ["Restore", "Core", "Ready", "2026-09-11"], ["Visual QA", "Quality", "Planned", "2026-09-15"],
  ] });
  table.styleOptions = { headerRow: true, bandedRows: true, firstColumn: true };
  table.cells.block({ row: 0, column: 0, rowCount: 5, columnCount: 4 }).assign({ textStyle: { fontSize: 16 } });
  table.borders.assign({ style: "solid", fill: palette.rule, width: 1 });
  table.cells.block({ row: 0, column: 0, rowCount: 1, columnCount: 4 }).assign({ fill: palette.ink, textStyle: { color: "white", bold: true, fontSize: 16 } });
  table.cells.block({ row: 1, column: 2, rowCount: 1, columnCount: 1 }).assign({ fill: "#DCFCE7", textStyle: { color: "#166534", bold: true } });
  table.cells.block({ row: 2, column: 2, rowCount: 1, columnCount: 1 }).assign({ fill: "#FEF3C7", textStyle: { color: "#92400E", bold: true } });
  slide.charts.add("bar", { position: { left: 710, top: 218, width: 472, height: 310 }, title: "Coverage by asset type", categories: ["Text", "Notes", "Table", "Chart", "Connector"], series: [{ name: "Checks", values: [12, 4, 2, 2, 3], fill: palette.accent }], barOptions: { direction: "bar", grouping: "clustered", gapWidth: 46 }, hasLegend: false, xAxis: { visible: false, majorGridlines: null }, yAxis: { textStyle: { fill: palette.muted, fontSize: 14 }, line: { style: "solid", fill: palette.rule, width: 1 } }, dataLabels: { showValue: true, position: "outEnd", textStyle: { fill: palette.ink, fontSize: 14, bold: true } } });
  addFooter(slide, "03");
  slide.speakerNotes.textFrame.setText(["[Sources]", "Synthetic metrics are deliberately non-production values."]);
  slide.speakerNotes.setVisible(true);
}

{
  const slide = deck.slides.add();
  slide.background.fill = "white";
  addTitle(slide, "Picture and conclusion", "Rich runs and a tiny PNG exercise media preservation; text remains editable.");
  const transparentPng = Uint8Array.from(Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9WlG5nAAAAAASUVORK5CYII=", "base64"));
  slide.images.add({ blob: transparentPng.buffer.slice(transparentPng.byteOffset, transparentPng.byteOffset + transparentPng.byteLength), contentType: "image/png", alt: "Embedded PNG sentinel for package preservation", fit: "cover", position: { left: 72, top: 222, width: 330, height: 250 }, geometry: "rect" });
  const imageFrame = slide.shapes.add({ geometry: "rect", name: "picture-frame", position: { left: 72, top: 222, width: 330, height: 250 }, fill: "none", line: { style: "solid", fill: palette.rule, width: 1 } });
  imageFrame.text = "Embedded image sentinel";
  imageFrame.text.style = { color: palette.muted, fontSize: 16, alignment: "center", verticalAlignment: "middle" };
  const conclusion = addLabeledBox(slide, "fixture-conclusion", "Conclusion", "Markdown can safely carry editable slide copy when the original PPTX remains the source of truth.", { left: 466, top: 222, width: 716, height: 116 }, palette.accentSoft);
  conclusion.text.set([
    [{ run: "Conclusion", textStyle: { bold: true, color: palette.accent, fontSize: "20pt" } }],
    [{ run: "Markdown can safely carry ", textStyle: { color: palette.ink, fontSize: "18pt" } }, { run: "editable slide copy", textStyle: { bold: true, underline: "sng", color: palette.ink, fontSize: "18pt" } }, { run: " when the original PPTX remains the source of truth.", textStyle: { color: palette.ink, fontSize: "18pt" } }],
  ]);
  const checklist = addText(slide, "final-checklist", "", { left: 486, top: 376, width: 650, height: 142 }, { fontSize: 18, color: palette.ink });
  checklist.text.set([
    { bulletCharacter: "•", marginLeft: 24, indent: -12, runs: ["MD projection keeps semantic titles and bullets."] },
    { bulletCharacter: "•", marginLeft: 24, indent: -12, runs: ["DRMD preserves image media and source provenance."] },
    { bulletCharacter: "•", marginLeft: 24, indent: -12, runs: ["F0 restores bytes; F1 keeps non-text parts unchanged."] },
  ]);
  checklist.text.style = { fontSize: 18, color: palette.ink };
  addFooter(slide, "04");
  slide.speakerNotes.textFrame.setText(["[Sources]", "Synthetic fixture; image bytes are an embedded package sentinel only."]);
  slide.speakerNotes.setVisible(true);
}

const pptx = await PresentationFile.exportPptx(deck);
await pptx.save(outputPath);
