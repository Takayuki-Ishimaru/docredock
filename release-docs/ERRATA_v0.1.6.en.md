# v0.1.6 errata

v0.1.6 Public Beta had material visual-semantics limits:

- common DOCX anchor/VML flows could lose connectors;
- standalone XLSX `rightArrow` shapes could promote a false distant edge; and
- PDF vector direction and duplicate handling did not provide a conservative semantic gate.

v0.1.7 applies consistent conservative rules across supported formats and avoids asserting ambiguous relations. Review diagnostics and the source document; do not treat v0.1.6 Mermaid output as a complete diagram.
