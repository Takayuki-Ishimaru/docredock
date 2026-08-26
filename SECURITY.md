# Security Policy / セキュリティポリシー

## 日本語

### 対応バージョン

DocRedock は現在 Public Beta です。最新の `0.1.x` リリースのみをセキュリティ更新の対象とします。修正は必要に応じて新しいBetaとして公開します。

### 脆弱性の報告

脆弱性の疑いがある場合は、公開Issueへ詳細や機密文書を投稿せず、GitHubの [Private vulnerability reporting](https://github.com/Takayuki-Ishimaru/docredock/security/advisories/new) を利用してください。

次の情報を、共有して安全な範囲で含めてください。

- 影響を受けるDocRedockのバージョン、OS、CPU
- 再現手順と期待結果／実際の結果
- 想定される影響と攻撃条件
- 機密情報を除去した最小の合成fixture
- 既知の場合は回避策

初回の受領確認は原則7日以内を目標とします。調査中は、修正版または緩和策が利用可能になるまで詳細の公開を控えてください。

### 対象

文書packageの解析、path traversal、archive bomb、外部relationship、数式／埋め込みobjectの扱い、worker境界、復元時のintegrity検証など、DocRedockが処理する信頼できない入力に関する問題を対象とします。第三者ツール自体の問題は、それぞれの提供元にも報告してください。

## English

### Supported versions

DocRedock is currently a Public Beta. Security updates are provided only for the latest `0.1.x` release. Fixes may be published as a newer beta when needed.

### Reporting a vulnerability

Do not post vulnerability details or confidential documents in a public issue. Use GitHub [Private vulnerability reporting](https://github.com/Takayuki-Ishimaru/docredock/security/advisories/new) instead.

Include the following when it is safe to share:

- Affected DocRedock version, operating system, and CPU
- Reproduction steps and expected versus actual behavior
- Expected impact and required attack conditions
- A minimal synthetic fixture with confidential data removed
- Any known workaround

We aim to acknowledge the report within seven days. Please keep details private while the issue is investigated and until a fix or mitigation is available.

### Scope

Reports may cover untrusted document-package parsing, path traversal, archive bombs, external relationships, formulas or embedded objects, worker boundaries, and restore-integrity checks. Vulnerabilities in third-party tools should also be reported to their respective maintainers.
