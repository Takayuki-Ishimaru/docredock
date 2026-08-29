import unittest

from visual_semantics_assertions import assert_expectation, parse_markdown


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

    def test_rejects_unexpected_or_blank_semantics(self):
        markdown = """\
```mermaid
flowchart TD
    a[]
    b[END]
    a --- b
```
"""
        ok, detail = assert_expectation(markdown, {
            "node_labels": ["END"], "edges": [], "no_blank_labels": True,
            "allow_unexpected_edges": False,
        })
        self.assertFalse(ok)
        self.assertIn("blank", detail)


if __name__ == "__main__":
    unittest.main()
