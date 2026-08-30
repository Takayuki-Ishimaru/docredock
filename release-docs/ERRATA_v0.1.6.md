# v0.1.6 正誤表

v0.1.6 Public Beta には、実際の図形構造では次の制約がありました。

- 一般的な DOCX anchor/VML flow で connector を検出しない場合がある。
- XLSX の standalone `rightArrow` が誤った遠方 edge へ昇格する場合がある。
- PDF vector の方向・重複判定は保守的な semantic projection を保証していない。

v0.1.7では、対応形式に一貫した保守的な判定を適用し、曖昧な関係を確定しないよう改善しました。元文書と診断を確認し、v0.1.6のMermaidを完全な図とみなさないでください。
