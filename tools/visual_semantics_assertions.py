"""Small, deterministic parser for the Mermaid flowchart subset emitted by DocRedock."""
from __future__ import annotations

import re
from dataclasses import dataclass

FENCE = re.compile(r"\`\`\`mermaid\s*\n(.*?)\n\`\`\`", re.DOTALL | re.IGNORECASE)
NODE = re.compile(r"^\s*([A-Za-z_][\w.-]*)\s*(?:\[([^\]]*)\]|\{([^}]*)\}|\(\[([^]]*)\]\)|\[/([^/]*)/\])\s*$")
EDGE = re.compile(r"^\s*([A-Za-z_][\w.-]*)\s*(-->|---)(?:\|([^|]*)\|)?\s*([A-Za-z_][\w.-]*)\s*$")

@dataclass(frozen=True)
class Edge:
    source: str
    target: str
    direction: str
    label: str | None = None

@dataclass(frozen=True)
class Graph:
    direction: str
    nodes: dict[str, str]
    edges: tuple[Edge, ...]

def extract_mermaid(markdown: str) -> list[str]:
    return [match.group(1).strip() for match in FENCE.finditer(markdown)]

def parse_flowchart(mermaid: str) -> Graph:
    lines = [line.strip() for line in mermaid.splitlines() if line.strip()]
    if not lines or not re.match(r"^flowchart\s+(LR|TD)\s*$", lines[0], re.IGNORECASE):
        raise ValueError("expected a DocRedock flowchart LR or TD header")
    nodes: dict[str, str] = {}
    edges: list[Edge] = []
    for line in lines[1:]:
        node = NODE.match(line)
        if node:
            node_id = node.group(1)
            label = next(value for value in node.groups()[1:] if value is not None).strip().strip('"')
            nodes[node_id] = label
            continue
        edge = EDGE.match(line)
        if edge:
            source, connector, label, target = edge.groups()
            edges.append(Edge(source, target, "directed" if connector == "-->" else "undirected", label.strip() if label else None))
    return Graph(lines[0].split()[1].upper(), nodes, tuple(edges))

def parse_markdown(markdown: str) -> list[Graph]:
    return [parse_flowchart(text) for text in extract_mermaid(markdown) if text.lstrip().lower().startswith("flowchart")]

def assert_expectation(markdown: str, expectation: dict) -> tuple[bool, str]:
    graphs = parse_markdown(markdown)
    if len(graphs) != 1:
        return False, f"expected exactly one flowchart Mermaid fence, found {len(graphs)}"
    graph = graphs[0]
    labels = list(graph.nodes.values())
    expected_labels = expectation.get("node_labels", [])
    missing_labels = [label for label in expected_labels if label not in labels]
    if missing_labels:
        return False, "missing node labels: " + ", ".join(missing_labels)
    if expectation.get("no_blank_labels") and any(not label.strip() for label in labels):
        return False, "blank Mermaid node label"
    if len(set(labels)) != len(labels):
        return False, "duplicate Mermaid node label"
    if "exact_node_count" in expectation and len(graph.nodes) != expectation["exact_node_count"]:
        return False, f"node count={len(graph.nodes)} (want {expectation['exact_node_count']})"
    if "exact_edge_count" in expectation and len(graph.edges) != expectation["exact_edge_count"]:
        return False, f"edge count={len(graph.edges)} (want {expectation['exact_edge_count']})"
    by_label = {node_id: label for node_id, label in graph.nodes.items()}
    actual = {(by_label.get(edge.source, edge.source), by_label.get(edge.target, edge.target), edge.direction, edge.label) for edge in graph.edges}
    wanted = {(edge["from"], edge["to"], edge.get("direction", "directed"), edge.get("label")) for edge in expectation.get("edges", [])}
    missing = wanted - actual
    unexpected = actual - wanted
    if missing:
        return False, "missing edges: " + repr(sorted(missing))
    if unexpected and not expectation.get("allow_unexpected_edges", True):
        return False, "unexpected edges: " + repr(sorted(unexpected))
    return True, f"nodes={len(graph.nodes)} edges={len(graph.edges)}"

def evaluate(markdown: str, expectation: dict) -> dict:
    ok, detail = assert_expectation(markdown, expectation)
    return {"pass": ok, "detail": detail}
