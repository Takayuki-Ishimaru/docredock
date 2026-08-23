---
rtmd_schema: 1.0
rtmd_rules: 1.0
document_id: doc_example
source_format: docx
roundtrip_store: document.rtmd
---
<!--rtmd:partition-begin id=part-0001 baseline_nodes=2-->
<!--rtmd:block id=n_heading kind=paragraph-->
本文
<!--rtmd:document-end id=doc_example partitions=1-->
追記はdocument-end後なので不正。blockのkindもbaselineから変更されています。
