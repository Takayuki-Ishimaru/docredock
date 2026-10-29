# Word revision defaults / Word変更履歴の既定値

This page is the canonical record of revision defaults. Unless a caller supplies `Revisions` in the API or `--revisions` in the CLI, the selected content policy determines the view as follows.

このページを変更履歴の既定値の正本とします。APIの`Revisions`またはCLIの`--revisions`を指定しない場合、選択した内容ポリシーによって以下の表示になります。

| Content policy / 内容ポリシー | CLI default / CLIの既定値 | GUI default / GUIの既定値 | API default (`Revisions: null`) / APIの既定値 |
| --- | --- | --- | --- |
| `visible` | `markup` | `markup` | `markup` |
| `complete` | `markup` | `markup` | `markup` |
| `sanitized` | `final` | `final` | `final` |

`markup` displays tracked changes and can include deleted text, authors and timestamps. Review the result before sharing it. `final` omits deleted text and revision annotations. `reject` stops the export when tracked changes are present. Under `sanitized`, choosing `markup` requires explicit sharing consent.

`markup`は変更履歴を表示し、削除した文字・著者・日時を含む場合があります。共有前に出力を確認してください。`final`は削除した文字と変更履歴の注記を除外します。`reject`は変更履歴がある文書の書き出しを止めます。`sanitized`で`markup`を選ぶ場合は、共有の明示許可が必要です。
