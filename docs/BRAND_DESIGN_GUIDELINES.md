# DocRedock ブランド／デザインガイドライン

Version: 1.0
Status: Active
Scope: DocRedock のアプリ UI、ドキュメント、Web、GitHub、配布物、宣伝素材

この文書は DocRedock の名前、色、画像資産、UI の判断基準をまとめた実装向けのリファレンスです。色の出典はリポジトリ内の [`assets/brand/docredock/source/BRAND_PALETTE_DAWN.md`](../assets/brand/docredock/source/BRAND_PALETTE_DAWN.md) です。

## 1. ブランドの核 / Brand

正式表記は **DocRedock**（大文字の `D` と `R`）です。製品名を `Docredock`、`DOCREDOCK`、`Doc Redock` と表記しないでください。

DocRedock は、周回する矢印と犬のシルエットを組み合わせた「見つける・追跡する・もう一度つなぐ」印象を持つ製品マークです。製品の個性はシンボルの形で表し、ブランドファミリーとしての一貫性は Dawn（夜明け）パレット、落ち着いたフラット表現、強い暗色構造で保ちます。

短い原則は **Same dawn, different silhouettes. / 同じ夜明け、異なるシルエット** です。

## 2. カラートークン / Color tokens

値は CSS の 16 進表記をそのまま正とします。`#000000` ではなく `--brand-black` を優先してください。

| Token | 表示名 | Hex | 役割 |
| --- | --- | --- | --- |
| `--brand-orange` | Dawn Orange | `#FF6B1A` | 主アクセント、CTA、アクティブ、重要なハイライト |
| `--brand-lavender` | Dawn Lavender | `#C6A7E8` | 補助面、ソフトなハイライト、二次アクセント |
| `--brand-blue` | Pre-dawn Blue | `#4057D6` | 主な技術色、リンク、情報、構造 |
| `--brand-black` | Silhouette Black | `#191827` | 文字、ナビゲーション、輪郭、暗い面 |
| `--brand-mist` | Morning Mist | `#F2EEF5` | ライト背景、カード、余白、非アクティブ面 |

```css
:root {
  --brand-orange: #FF6B1A;
  --brand-lavender: #C6A7E8;
  --brand-blue: #4057D6;
  --brand-black: #191827;
  --brand-mist: #F2EEF5;

  --color-primary: var(--brand-blue);
  --color-accent: var(--brand-orange);
  --color-secondary: var(--brand-lavender);
  --color-foreground: var(--brand-black);
  --color-background: var(--brand-mist);
}
```

### 使い分け

- 背景は Morning Mist または白、本文・見出しは Silhouette Black とする。
- 通常のリンク、情報、選択中の構造には Pre-dawn Blue を使う。
- Dawn Orange は意味を持つ小さな面に限定する。画面全体の背景色にはしない。
- Lavender は強い色同士の間をやわらげる面・補助表現にする。
- 目安の配分は Black 35–45%、Blue 20–30%、Lavender 15–25%、Orange 8–15%。厳密な計算ではなく、Orange をベースにしないことが重要です。

販促素材では次のグラデーションを利用できますが、マスター・ロゴは必ずフラット色でも成立させます。

```css
--gradient-dawn: linear-gradient(120deg, #191827 0%, #4057D6 38%, #C6A7E8 68%, #FF6B1A 100%);
--gradient-soft-dawn: linear-gradient(135deg, #F2EEF5 0%, #C6A7E8 50%, #4057D6 100%);
--gradient-dark-hero: linear-gradient(135deg, #191827 0%, #24213A 45%, #4057D6 100%);
```

## 3. タイポグラフィ / Typography

- UI の第一候補は OS の system sans-serif。日本語では `Noto Sans JP` を優先し、フォールバックに `system-ui`, `-apple-system`, `Segoe UI`, `sans-serif` を置く。
- 本文は 14–16px、行高 1.5–1.7。見出しは太さ 600–700、本文は 400–500 を基準にする。
- 画面内の見出しは短く、動詞を使った明確なラベルにする。全大文字や長い斜体は避ける。
- ロゴの文字やマークを再入力して再現しない。ロゴ画像は `logo/png` の提供資産を使う。

## 4. レイアウトとコンポーネント / Layout & components

- 4px を基底単位とし、通常の間隔は 8 / 12 / 16 / 24 / 32px。関連する内容を近づけ、セクションを 24px 以上で分ける。
- 小画面では余白より本文の可読性を優先し、横スクロールを発生させない。カードやバナーは角を適度に丸め、マークの円形・矢印モチーフと調和させる。
- 主ボタンは Pre-dawn Blue 面＋読みやすい文字。最重要の一操作だけ Dawn Orange のアクセントを許可する。
- 二次ボタン、タグ、補助カードは Lavender または Mist。暗い面では Black を土台に Blue/Lavender を情報の階層として使う。
- ナビゲーションは構造色として Silhouette Black、選択中は Blue、行動の強調は Orange とする。
- 重要な操作にはテキストを伴う。色だけで状態や意味を伝えない。

### 状態 / States

`default` は Mist/White と Black、`hover` は面の明度または Blue の濃度をわずかに変え、`focus-visible` は 2px 以上の明確なアウトライン（Blue または Orange）を表示します。`active/selected` は Blue の面または Orange の小さなインジケーター、`disabled` は彩度と不透明度を下げつつコントラストを保ちます。`loading` は固定レイアウトの進捗表示とし、点滅だけに依存しません。`error/success/warning` は追加色だけでなく、ラベル、アイコン、説明文を必ず併用し、色を追加する場合はこのガイドラインとコントラスト検証を更新します。

## 5. アクセシビリティ / Accessibility

- 本文と背景のコントラストは WCAG 2.2 AA（通常文字 4.5:1、太字・大きな文字 3:1）を目標にする。ブランド色を優先して読みにくくしない。
- Orange や Lavender を本文色に使う前に実測する。迷ったら Black または Blue の文字を使う。
- キーボード操作では `focus-visible` を隠さず、操作対象には 44×44px 程度のタッチ領域を確保する。
- 画像には用途に応じた代替テキストを付ける。装飾だけなら空の alt とし、意味を持つロゴは `DocRedock` と説明する。スクリーンリーダーで重複するロゴ名を読み上げない。
- アニメーションやグラデーションは控えめにし、`prefers-reduced-motion` を尊重する。

## 6. アイコンとロゴ / Icon usage

- 製品マークは、青いハウンド、Lavender の耳、Orange の鼻・アクセント、Black の周回矢印という視覚的な主役を保つ。
- 小サイズでは細部を増やさず、輪郭と矢印が判別できるかを 16/24/32px で確認する。必要に応じて `app-icons` や `web` の既成サイズを使う。
- ロゴを引き伸ばす、斜めにする、色を置き換える、縁取りや影を足す、矢印や耳を切り抜く加工は禁止。
- ロゴ透明版は背景との十分なコントラストを確保する。正方形アイコンはアプリランチャー、透明版は文書・Web・販促の再利用に使う。
- アイコン 1 個につきパレット 3–5 色を目安にし、Orange は原則 1 つの焦点領域に制限する。第三者（Microsoft、Windows、OneDrive 等）のマークを模倣しない。

## 7. 資産インベントリとパス規約 / Asset inventory & paths

すべての配布元ファイルは `assets/brand/docredock/` に保管します。元パックの分類を保ちつつ、Git では目的別の小文字ディレクトリに整理しています。

| パス | 内容 | 主な用途 |
| --- | --- | --- |
| `source/` | マスター PNG 3点、`BRAND_PALETTE_DAWN.md` | 原本・色の参照 |
| `app-icons/png/` | `DocRedock-appicon-*` 16–1024px | アプリ／デスクトップの各サイズ |
| `app-icons/ico/` | `DocRedock-appicon.ico` | アプリ用 Windows ICO |
| `logo/png/` | 透明ロゴ 32–2048px と master | 再利用可能なマーク |
| `web/` | favicon、Apple touch、Android Chrome、PWA manifest、Windows XML | Web/PWA |
| `banners/dark/`, `banners/light/` | 各 1200×400–2400×800 と master | ヒーロー・ヘッダー |
| `social/dark/`, `social/light/` | GitHub、OG、X、LinkedIn 比率 | SNS・共有カード |
| `windows/` | PNG 16–256px と `DocRedock.ico` | Windows 配布物 |
| `meta/README.txt` | パック内容・用途の原文メタデータ | 由来の確認 |

画像は元の PNG/ICO 形式とファイル名を維持します。新規派生物は用途とピクセル寸法を名前に含め、既存マスターを上書きしません。`.DS_Store` やビルド生成物は保管しません。アプリ実行時に必要なコピーは [`src/DocRedock.Gui/Assets/`](../src/DocRedock.Gui/Assets/) の `DocRedock-appicon-64x64.png` と `DocRedock.ico` に限ります。

## 8. Do / Don't

### Do

- `DocRedock` の綴りと大文字を守る。
- 構造に Black、技術的な情報に Blue、重要な操作に限定して Orange、補助面に Lavender、背景に Mist を使う。
- ライト／ダークの既成バナーを背景に合わせて選び、スクリーンショット自体は過度に色付けしない。
- 変更や追加の画像は目的、サイズ、背景（light/dark）、ライセンス／出典を確認して `assets/brand/docredock/` に置く。

### Don't

- Orange の全面背景、純粋な黒、ネオン多色、全体が青だけの SaaS 表現にしない。
- ロゴを再描画・変形・着色したり、グロス、ベベル、過剰な発光を足したりしない。
- 色だけで成功・失敗・選択状態を表さない。
- GitHub ソーシャル画像やアイコンに無関係な文言、第三者ロゴ、低コントラストの文字を重ねない。

## 9. 変更管理

色の変更は既存資産、UI、コントラスト、配布物への影響が大きいため、まず canonical palette とこの文書を同時に更新し、派生画像を再生成します。製品固有の追加色や新しいロゴ案は、DocRedock の Dawn ファミリーから外れる理由と適用範囲を記録してから採用してください。
