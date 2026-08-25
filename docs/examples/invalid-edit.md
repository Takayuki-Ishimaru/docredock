---
drmd_schema: 1.0
drmd_rules: 1.0
document_id: doc_example
source_format: docx
roundtrip_store: document.drmd
---
<!--drmd:partition-begin id=part-0001 baseline_nodes=2-->
<!--drmd:block id=n_heading kind=paragraph-->
本文
<!--drmd:document-end id=doc_example partitions=1-->
追記はdocument-end後なので不正。blockのkindもbaselineから変更されています。
