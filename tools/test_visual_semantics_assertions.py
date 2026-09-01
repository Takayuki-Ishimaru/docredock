import tempfile
import unittest
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

from visual_semantics_assertions import assert_expectation, parse_markdown
from visual_semantics_qa import (
    CliRunResult,
    FIXED_SEED,
    FORMATS,
    REQUIRED_PERTURBATIONS,
    RelationCase,
    build_evidence,
    classify_tier,
    expected_relations,
    generate_perturbation_corpus,
    materialize_perturbation_corpus,
    metrics,
    mode_for_spec,
    run_materialized_corpus,
    validate_corpus,
)


class VisualSemanticsAssertionsTests(unittest.TestCase):
    def test_parses_docredock_flowchart_and_exact_labeled_edges(self):
        markdown = """\
```mermaid
flowchart LR
    start([START])
    check{CHECK}
    start --> check
```
"""
        graph = parse_markdown(markdown)[0]
        self.assertEqual({"start": "START", "check": "CHECK"}, graph.nodes)
        ok, detail = assert_expectation(markdown, {
            "node_labels": ["START", "CHECK"],
            "edges": [{"from": "START", "to": "CHECK", "direction": "directed"}],
            "exact_node_count": 2, "exact_edge_count": 1,
            "no_blank_labels": True, "allow_unexpected_edges": False,
        })
        self.assertTrue(ok, detail)

    def test_corpus_is_fixed_complete_and_deterministic(self):
        first = generate_perturbation_corpus()
        self.assertEqual(first, generate_perturbation_corpus())
        self.assertEqual(FIXED_SEED, 20260830)
        validate_corpus(first)
        required_gaps = {0, 2, 5, 10, 20, 35, 50}
        for format_name in ("docx", "pptx", "xlsx", "pdf"):
            self.assertEqual(required_gaps, {
                item.parameter for item in first
                if item.format == format_name and item.operation == "endpoint-gap"
            })
            self.assertTrue(REQUIRED_PERTURBATIONS.issubset({
                item.operation for item in first if item.format == format_name
            }))

    def test_pdf_uses_the_same_geometry_driven_tier_classification_as_every_other_format(self):
        # F3-3: PDF used to be force-classified ambiguous (Tier C) for every perturbation via a
        # blanket "or format_name == 'pdf'" clause, regardless of gap/rotation geometry. That
        # meant tier_gate()'s Tier A/B recall gates never actually applied to PDF. PDF must now
        # land on the exact same tier as docx/pptx/xlsx for the same geometry parameters, and
        # gap=0 (no perturbation at all) must be Tier A rather than an automatic Tier C.
        corpus = generate_perturbation_corpus()
        by_key = {(item.format, item.operation, item.parameter): item.tier for item in corpus}
        geometry_cases = ([("endpoint-gap", gap) for gap in (0, 2, 5, 10, 20, 35, 50)]
                          + [("rotation", rotation) for rotation in (-30, -15, -5, 5, 15, 30, 90)]
                          + [("label-offset", offset) for offset in (5, 20, 50)])
        for operation, parameter in geometry_cases:
            tiers = {by_key[(fmt, operation, parameter)] for fmt in FORMATS}
            self.assertEqual(1, len(tiers), f"{operation}={parameter} tier diverged across formats: {tiers}")
        pdf_baseline = next(item for item in corpus if item.format == "pdf"
                            and item.operation == "endpoint-gap" and item.parameter == 0)
        self.assertEqual("A", pdf_baseline.tier)
        self.assertEqual(classify_tier(), pdf_baseline.tier)

    def test_materializes_real_format_structures_deterministically(self):
        corpus = generate_perturbation_corpus()
        with tempfile.TemporaryDirectory() as first_dir, tempfile.TemporaryDirectory() as second_dir:
            first = materialize_perturbation_corpus(Path(first_dir), corpus)
            second = materialize_perturbation_corpus(Path(second_dir), corpus)
            self.assertEqual([path.read_bytes() for path in first],
                             [path.read_bytes() for path in second])
            by_format = {name: next(path for path in first if path.suffix == f".{name}")
                         for name in ("docx", "pptx", "xlsx", "pdf")}
            with zipfile.ZipFile(by_format["docx"]) as archive:
                self.assertIn("word/document.xml", archive.namelist())
                ET.fromstring(archive.read("word/document.xml"))
                self.assertFalse(any("visual-semantics.json" in name for name in archive.namelist()))
            with zipfile.ZipFile(by_format["pptx"]) as archive:
                self.assertIn("ppt/presentation.xml", archive.namelist())
                self.assertIn("ppt/slides/slide1.xml", archive.namelist())
                ET.fromstring(archive.read("ppt/slides/slide1.xml"))
            with zipfile.ZipFile(by_format["xlsx"]) as archive:
                self.assertIn("xl/workbook.xml", archive.namelist())
                self.assertIn("xl/worksheets/sheet1.xml", archive.namelist())
                self.assertIn("xl/drawings/drawing1.xml", archive.namelist())
                ET.fromstring(archive.read("xl/drawings/drawing1.xml"))
            pdf = by_format["pdf"].read_bytes()
            self.assertIn(b"/Type /Catalog", pdf)
            self.assertIn(b"/Contents 5 0 R", pdf)
            self.assertIn(b"stream\n", pdf)
            self.assertIn(b"xref\n", pdf)
            self.assertNotIn(b"VISUAL_SEMANTICS_JSON", pdf)

    def test_only_office_baselines_keep_native_connector_ids(self):
        corpus = generate_perturbation_corpus(("docx", "pptx", "xlsx"))
        part_names = {
            "docx": "word/document.xml",
            "pptx": "ppt/slides/slide1.xml",
            "xlsx": "xl/drawings/drawing1.xml",
        }
        with tempfile.TemporaryDirectory() as directory:
            paths = materialize_perturbation_corpus(Path(directory), corpus)
            for spec, path in zip(corpus, paths):
                with zipfile.ZipFile(path) as archive:
                    drawing = archive.read(part_names[spec.format])
                baseline = (spec.operation == "endpoint-gap"
                            and spec.gap_percent_minor_axis == 0)
                self.assertEqual(baseline, b"<a:stCxn" in drawing, spec.case_id)
                self.assertEqual(baseline, b"<a:endCxn" in drawing, spec.case_id)
                expected_mode = ("native-only" if baseline else
                                 "balanced" if spec.tier == "C" else "safe")
                self.assertEqual(expected_mode, mode_for_spec(spec), spec.case_id)

    def test_xlsx_operations_are_materialized_in_drawing_xml(self):
        corpus = generate_perturbation_corpus(("xlsx", "pdf"))
        with tempfile.TemporaryDirectory() as directory:
            paths = materialize_perturbation_corpus(Path(directory), corpus)
            by_id = {spec.case_id: path for spec, path in zip(corpus, paths)}

            def xlsx_xml(spec):
                with zipfile.ZipFile(by_id[spec.case_id]) as archive:
                    return archive.read("xl/drawings/drawing1.xml")

            xlsx_specs = [spec for spec in corpus if spec.format == "xlsx"]
            gaps = [spec for spec in xlsx_specs if spec.operation == "endpoint-gap"]
            self.assertEqual(7, len({xlsx_xml(spec) for spec in gaps}))

            rotation = next(spec for spec in xlsx_specs
                            if spec.operation == "rotation" and spec.parameter == -30)
            self.assertIn(b'rot="-1800000"', xlsx_xml(rotation))

            translations = [spec for spec in xlsx_specs if spec.operation == "translation"]
            translation_xml = [xlsx_xml(spec) for spec in translations]
            self.assertEqual(len(translations), len(set(translation_xml)))
            for spec, drawing in zip(translations, translation_xml):
                expected = (f'<a:off x="{1000000 + int(spec.translation_x * 10000)}" '
                            f'y="{1000000 + int(spec.translation_y * 10000)}"/>').encode()
                self.assertIn(expected, drawing)

            horizontal = next(spec for spec in xlsx_specs if spec.operation == "flip-horizontal")
            vertical = next(spec for spec in xlsx_specs if spec.operation == "flip-vertical")
            self.assertIn(b'flipH="1"', xlsx_xml(horizontal))
            self.assertIn(b'flipV="1"', xlsx_xml(vertical))

            baseline = next(spec for spec in gaps if spec.parameter == 0)
            removed = next(spec for spec in xlsx_specs if spec.operation == "native-id-removed")
            self.assertIn(b"<a:stCxn", xlsx_xml(baseline))
            self.assertIn(b"<a:endCxn", xlsx_xml(baseline))
            self.assertNotIn(b"<a:stCxn", xlsx_xml(removed))
            self.assertNotIn(b"<a:endCxn", xlsx_xml(removed))

            labels = [spec for spec in xlsx_specs if spec.operation == "label-offset"]
            self.assertEqual(3, len({xlsx_xml(spec) for spec in labels}))
            grouped = next(spec for spec in xlsx_specs if spec.operation == "grouped")
            self.assertIn(b"<xdr:grpSp>", xlsx_xml(grouped))

            pdf_group = next(spec for spec in corpus
                             if spec.format == "pdf" and spec.operation == "grouped")
            pdf = by_id[pdf_group.case_id].read_bytes()
            self.assertIn(b"/DiagramGroup BMC", pdf)
            self.assertIn(b"EMC", pdf)

    def test_expected_relations_cover_ambiguous_nodes_and_edge_labels(self):
        corpus = generate_perturbation_corpus(("pptx",))
        competing = next(spec for spec in corpus if spec.operation == "competing-node")
        intermediate = next(spec for spec in corpus if spec.operation == "intermediate-node")
        label = next(spec for spec in corpus
                     if spec.operation == "label-offset" and spec.parameter == 5)
        far_label = next(spec for spec in corpus
                         if spec.operation == "label-offset" and spec.parameter == 50)
        textless = next(spec for spec in corpus if spec.operation == "textless")
        self.assertEqual((), expected_relations(competing))
        self.assertEqual((), expected_relations(intermediate))
        self.assertEqual((("START", "END", "directed", "YES"),),
                         expected_relations(label))
        self.assertEqual((("START", "END", "directed", None),),
                         expected_relations(far_label))
        pdf_separated = next(spec for spec in generate_perturbation_corpus(("pdf",))
                             if spec.operation == "arrowhead-separated")
        self.assertEqual((("START", "END", "directed", None),),
                         expected_relations(pdf_separated))
        self.assertEqual((), expected_relations(textless))

    def test_textless_synthetic_cli_relation_is_diagnostic_not_false_edge(self):
        spec = next(item for item in generate_perturbation_corpus(("docx",))
                    if item.operation == "textless")
        markdown = """\
```mermaid
flowchart LR
    a[Shape 2]
    b[Shape 3]
    a --> b
```
"""
        with tempfile.TemporaryDirectory() as directory:
            cases, records = run_materialized_corpus(
                Path(directory), (spec,),
                lambda *_: CliRunResult(markdown, "VisualNodeLabelMissing", 1),
                parse_markdown,
            )
        self.assertEqual((), cases[0].observed_relations)
        self.assertEqual(1, cases[0].unresolved_expected)
        self.assertEqual(1, cases[0].diagnosed_unresolved)
        self.assertEqual("passed", records[0]["status"])

    def test_ambiguous_negative_edge_is_not_hidden_by_fallback_diagnostic(self):
        spec = next(item for item in generate_perturbation_corpus(("pptx",))
                    if item.operation == "competing-node")
        markdown = """\
```mermaid
flowchart LR
    a[START]
    b[END]
    a --> b
```
"""
        with tempfile.TemporaryDirectory() as directory:
            cases, records = run_materialized_corpus(
                Path(directory), (spec,),
                lambda *_: CliRunResult(
                    markdown, "VisualSemanticProjectionFallback", 1
                ),
                parse_markdown,
            )
        self.assertEqual((("START", "END", "directed", None),),
                         cases[0].observed_relations)
        self.assertEqual(1, cases[0].unresolved_expected)
        self.assertEqual(1, cases[0].diagnosed_unresolved)
        self.assertEqual("failed", records[0]["status"])

    def test_runner_executes_every_case_once_and_interprets_cli_output(self):
        corpus = generate_perturbation_corpus()
        calls = []

        def fake_runner(spec, fixture, mode):
            calls.append((spec.case_id, fixture.suffix, mode))
            expected = expected_relations(spec)
            if not expected:
                return CliRunResult("", "VisualConnectorUnresolved", 1,
                                    "fake-sha", "fake-sha", True)
            ids = {"START": "s", "END": "e", "SECOND": "s2", "THIRD": "e2"}
            lines = ["```mermaid", "flowchart LR"]
            for label in sorted({value for relation in expected for value in relation[:2]}):
                lines.append(f"    {ids[label]}[{label}]")
            for source, target, direction, label in expected:
                connector = "-->" if direction == "directed" else "---"
                edge_label = f"|{label}|" if label else ""
                lines.append(f"    {ids[source]} {connector}{edge_label} {ids[target]}")
            lines.append("```")
            return CliRunResult("\n".join(lines), "", 0, "fake-sha", "fake-sha", True)

        with tempfile.TemporaryDirectory() as directory:
            cases, records = run_materialized_corpus(
                Path(directory), corpus, fake_runner, parse_markdown
            )
        self.assertEqual(len(corpus), len(calls))
        self.assertEqual(len(corpus), len({case_id for case_id, _, _ in calls}))
        self.assertEqual([mode_for_spec(item) for item in corpus],
                         [mode for _, _, mode in calls])
        self.assertTrue(all(record["execution"] == "docredock-cli-export" for record in records))
        self.assertTrue(all(record["status"] == "passed" for record in records))
        self.assertTrue(all(record["deterministic"] is True for record in records))
        self.assertTrue(all(record["output_sha256"] == record["repeat_output_sha256"]
                            for record in records))
        self.assertEqual(len(corpus), len(cases))

    def test_non_deterministic_cli_output_fails_the_case(self):
        spec = next(item for item in generate_perturbation_corpus(("docx",))
                    if item.operation == "endpoint-gap" and item.parameter == 2)
        markdown = """\
```mermaid
flowchart LR
    s[START]
    e[END]
    s --> e
```
"""
        with tempfile.TemporaryDirectory() as directory:
            _, records = run_materialized_corpus(
                Path(directory), (spec,),
                lambda *_: CliRunResult(markdown, "", 0, "first", "second", False),
                parse_markdown,
            )
        self.assertEqual("failed", records[0]["status"])
        self.assertFalse(records[0]["deterministic"])

    def test_pdf_partial_vector_topology_warning_does_not_discard_a_resolved_relation(self):
        # PdfTextExtractor.Extract() (src/DocRedock.Formats.Pdf/PdfTextExtractor.cs) raises
        # VisualSemanticProjectionUnavailable whenever VisualGraph.Accounting.FallbackPaths > 0,
        # and a connector's own raw open-stroke VisualPath is *always* recorded IsFallback=true
        # (BuildVisualGraph: IsFallback = curveSeen || !isClosed || !painted) even once the
        # semantic edge built from that path resolves correctly with high confidence. So this
        # diagnostic fires on virtually every PDF connector diagram, resolved or not.
        # evaluate_cli_result() must not treat it as proof that nothing resolved (it used to,
        # which zeroed every PDF case's observed relations unconditionally -- see the F3-3
        # investigation notes and tools/release-smoke-test.py's exercise_pdf_render, which
        # already tolerates this exact code on a resolved vector PDF).
        spec = next(item for item in generate_perturbation_corpus(("pdf",))
                    if item.operation == "endpoint-gap" and item.parameter == 0)
        markdown = """\
```mermaid
flowchart LR
    a[START]
    b[END]
    a --> b
```
"""
        diagnostics = "WARNING VisualSemanticProjectionUnavailable: PDF page 1 contains partial vector topology."
        with tempfile.TemporaryDirectory() as directory:
            cases, records = run_materialized_corpus(
                Path(directory), (spec,),
                lambda *_: CliRunResult(markdown, diagnostics, 1, "sha", "sha", True),
                parse_markdown,
            )
        self.assertEqual((("START", "END", "directed", None),), cases[0].observed_relations)
        self.assertEqual("passed", records[0]["status"])

    def test_pdf_fallback_only_quality_still_discards_observed_relations(self):
        # VisualSemanticProjectionFallback (unlike VisualSemanticProjectionUnavailable above) is
        # raised only when ReadableMarkdownSerializer.WriteVisualGraph found VisualGraphQuality
        # FallbackOnly/Invalid and returned before ever emitting a mermaid block. That genuinely
        # means nothing was projected, so evaluate_cli_result() must keep discarding relations
        # parsed alongside it (a synthetic markdown block here stands in for what a real run
        # would never actually emit in that branch).
        spec = next(item for item in generate_perturbation_corpus(("pdf",))
                    if item.operation == "endpoint-gap" and item.parameter == 0)
        markdown = """\
```mermaid
flowchart LR
    a[START]
    b[END]
    a --> b
```
"""
        diagnostics = "WARNING VisualSemanticProjectionFallback: PDF page 1 is fallback-only."
        with tempfile.TemporaryDirectory() as directory:
            cases, records = run_materialized_corpus(
                Path(directory), (spec,),
                lambda *_: CliRunResult(markdown, diagnostics, 1),
                parse_markdown,
            )
        self.assertEqual((), cases[0].observed_relations)
        self.assertEqual("failed", records[0]["status"])

    def test_missing_relation_without_diagnostic_is_silent_loss(self):
        spec = next(item for item in generate_perturbation_corpus(("pdf",))
                    if item.operation == "endpoint-gap" and item.parameter == 50)
        with tempfile.TemporaryDirectory() as directory:
            cases, records = run_materialized_corpus(
                Path(directory), (spec,), lambda *_: CliRunResult("", "", 1), parse_markdown
            )
        values = metrics(cases)
        self.assertEqual(1.0, values["silent_loss_rate"]["value"])
        self.assertEqual("failed", records[0]["status"])

    def test_fractional_metrics_and_tier_gates_are_computed_from_relations(self):
        edge = ("START", "END", "directed", None)
        cases = (
            RelationCase("native", "docx", "A", (edge,), (edge,), (edge,), (edge,), projected_nodes=2),
            RelationCase("inferred", "pptx", "B", (edge,), (edge,), projected_nodes=2),
            RelationCase("diagnosed", "pdf", "C", (edge,), (), unresolved_expected=1,
                         diagnosed_unresolved=1, projected_nodes=2),
        )
        evidence = build_evidence(tag="v0.2.0", version="0.2.0", cases=cases)
        self.assertEqual("docredock-cli-export", evidence["execution"]["mode"])
        self.assertEqual(len(cases), evidence["execution"]["executed_case_count"])
        self.assertTrue(all(evidence["tiers"][tier]["gate"]["passed"] for tier in ("A", "B", "C")))

    def test_incorrect_and_missing_relations_change_fractions(self):
        expected = (("A", "B", "directed", None), ("B", "C", "directed", None))
        observed = (("A", "B", "directed", None), ("A", "C", "directed", None))
        values = metrics((RelationCase("bad", "docx", "B", expected, observed),))
        self.assertEqual(0.5, values["edge_precision"]["value"])
        self.assertEqual(0.5, values["edge_recall"]["value"])
        self.assertEqual(0.5, values["false_edge_rate"]["value"])


if __name__ == "__main__":
    unittest.main()
