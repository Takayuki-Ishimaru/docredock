---
rtmd_schema: 1.0
rtmd_rules: 1.0
document_id: doc_76b4976d551ffd47
source_format: xlsx
roundtrip_store: system-design-sample.rtmd
content_policy: visible
preserve_rtmd_comments: true
---
<!--rtmd:partition-begin id=sheet-概要 baseline_nodes=92-->
## Sheet: 概要

<!--rtmd:sheet-table range=A1:H21 baseline_nodes=92 editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula-->
| Row | A | B | C | D | E | F | G | H |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 注文管理プラットフォーム システム設計書 |  |  |  |  |  |  |  |
| 2 | レビュー用サンプル — 複数シート、参照式、入力規則を含む設計資料 |  |  |  |  |  |  |  |
| 3 |  |  |  |  |  |  |  |  |
| 4 | 文書ID | ARCH-OMS-001 |  | API数 | `=COUNTA('API設計'!$A$5:$A$10)` |  |  |  |
| 5 | 対象システム | 注文管理プラットフォーム |  | DB定義行数 | `=COUNTA('DB設計'!$A$5:$A$21)` |  |  |  |
| 6 | 版 | 1.2 |  | 未対応の非機能要件 | `=COUNTIF('非機能要件'!$H$5:$H$11,"未対応")` |  |  |  |
| 7 | 更新日 | 46256 |  | 可用性目標 | `='非機能要件'!D5` |  |  |  |
| 8 |  |  |  |  |  |  |  |  |
| 9 |  |  |  |  |  |  |  |  |
| 10 | アーキテクチャ構成 |  |  |  |  |  |  |  |
| 11 | レイヤー | コンポーネント | 責務 | 技術 | スケール方式 | オーナー | 可用性 | セキュリティ境界 |
| 12 | Edge | API Gateway | 認証・流量制御・ルーティング | Managed Gateway | リージョン冗長 | Platform | 99.99% | Public / OAuth2 |
| 13 | Application | Order API | 注文受付・状態遷移 | .NET 10 | 水平スケール | Order Team | 99.95% | Private subnet |
| 14 | Domain | Order Worker | 在庫引当・非同期処理 | .NET Worker | キュー長連動 | Order Team | 99.90% | Private subnet |
| 15 | Data | Order DB | 注文・明細・Outbox永続化 | PostgreSQL 17 | Multi-AZ | Data Team | 99.99% | Encrypted storage |
| 16 |  |  |  |  |  |  |  |  |
| 17 | 主要アーキテクチャ決定 |  |  |  |  |  |  |  |
| 18 | ADR | 決定 | 理由 | 状態 | オーナー | 決定日 | 影響 | 次回レビュー |
| 19 | ADR-001 | Outboxパターンを採用 | DB更新とイベント発行の不整合を防止 | 承認済 | Order Team | 46205 | 書込経路にOutboxテーブルを追加 | 46402 |
| 20 | ADR-002 | APIを同期受付・非同期処理に分離 | ピーク負荷と外部連携遅延を吸収 | 承認済 | Platform | 46221 | 202 Acceptedと状態照会APIが必要 | 46402 |
| 21 | ADR-003 | 注文IDはULID | 時系列ソートと分散採番を両立 | レビュー中 | Data Team | 46244 | 既存UUID連携先の互換性確認 | 46280 |

<!--rtmd:partition-end id=sheet-概要 baseline_nodes=92-->
<!--rtmd:partition-begin id=sheet-API設計 baseline_nodes=65-->
## Sheet: API設計

<!--rtmd:sheet-table range=A1:I10 baseline_nodes=65 editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula-->
| Row | A | B | C | D | E | F | G | H | I |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | API設計 |  |  |  |  |  |  |  |  |
| 2 | 外部公開・内部利用APIの契約、認証、応答時間目標を管理 |  |  |  |  |  |  |  |  |
| 3 |  |  |  |  |  |  |  |  |  |
| 4 | API ID | Method | Path | 概要 | 認証 | Request Schema | Response | SLA (ms) | Owner |
| 5 | API-001 | POST | /v1/orders | 注文を受け付ける | OAuth2 orders.write | CreateOrderRequest | 202 OrderAccepted | 300 | Order Team |
| 6 | API-002 | GET | /v1/orders/{orderId} | 注文状態を取得 | OAuth2 orders.read | Path: orderId | 200 Order | 200 | Order Team |
| 7 | API-003 | POST | /v1/orders/{orderId}/cancel | 未出荷注文を取消 | OAuth2 orders.write | CancelOrderRequest | 202 OrderAccepted | 300 | Order Team |
| 8 | API-004 | GET | /v1/orders | 顧客別注文一覧 | OAuth2 orders.read | Query: customerId,cursor | 200 OrderPage | 400 | Order Team |
| 9 | API-005 | POST | /internal/v1/reservations | 在庫引当を要求 | mTLS | ReservationRequest | 202 ReservationAccepted | 500 | Inventory Team |
| 10 | API-006 | GET | /health/ready | 依存先を含むReady判定 | Network allowlist | None | 200/503 Health | 100 | Platform |

<!--rtmd:partition-end id=sheet-API設計 baseline_nodes=65-->
<!--rtmd:partition-begin id=sheet-DB設計 baseline_nodes=120-->
## Sheet: DB設計

<!--rtmd:sheet-table range=A1:I21 baseline_nodes=120 editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula-->
| Row | A | B | C | D | E | F | G | H | I |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | DB設計 |  |  |  |  |  |  |  |  |
| 2 | 注文トランザクション境界内の主要テーブル・列・索引を定義 |  |  |  |  |  |  |  |  |
| 3 |  |  |  |  |  |  |  |  |  |
| 4 | Table | Column | Type | Nullable | Key | FK | Default | Index | Description |
| 5 | orders | order_id | char(26) | NO | PK |  |  | orders_pkey | ULID形式の注文ID |
| 6 | orders | customer_id | varchar(64) | NO |  |  |  | idx_orders_customer_created | 顧客ID |
| 7 | orders | status | varchar(24) | NO |  |  | 'accepted' | idx_orders_status | 注文状態 |
| 8 | orders | currency | char(3) | NO |  |  | 'JPY' |  | ISO 4217通貨コード |
| 9 | orders | total_amount | numeric(14,2) | NO |  |  | 0 |  | 税込合計金額 |
| 10 | orders | version | integer | NO |  |  | 1 |  | 楽観ロック用 |
| 11 | orders | created_at | timestamptz | NO |  |  | now() | idx_orders_customer_created | 作成日時 |
| 12 | orders | updated_at | timestamptz | NO |  |  | now() |  | 更新日時 |
| 13 | order_items | order_id | char(26) | NO | PK | orders.order_id |  | order_items_pkey | 注文ID |
| 14 | order_items | line_no | integer | NO | PK |  |  | order_items_pkey | 明細行番号 |
| 15 | order_items | sku | varchar(64) | NO |  |  |  | idx_order_items_sku | 商品SKU |
| 16 | order_items | quantity | integer | NO |  |  | 1 |  | 数量。1以上 |
| 17 | order_items | unit_price | numeric(14,2) | NO |  |  | 0 |  | 単価 |
| 18 | outbox_events | event_id | uuid | NO | PK |  | gen_random_uuid() | outbox_events_pkey | イベントID |
| 19 | outbox_events | aggregate_id | char(26) | NO |  | orders.order_id |  | idx_outbox_pending | 注文ID |
| 20 | outbox_events | event_type | varchar(128) | NO |  |  |  |  | イベント型 |
| 21 | outbox_events | payload | jsonb | NO |  |  |  |  | イベント本文 |

<!--rtmd:partition-end id=sheet-DB設計 baseline_nodes=120-->
<!--rtmd:partition-begin id=sheet-非機能要件 baseline_nodes=66-->
## Sheet: 非機能要件

<!--rtmd:sheet-table range=A1:H11 baseline_nodes=66 editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula-->
| Row | A | B | C | D | E | F | G | H |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 非機能要件 |  |  |  |  |  |  |  |
| 2 | 可用性・性能・復旧・セキュリティ・運用の受入基準 |  |  |  |  |  |  |  |
| 3 |  |  |  |  |  |  |  |  |
| 4 | NFR ID | カテゴリ | メトリクス | 目標値 | 単位 | 計測方法 | 優先度 | 状態 |
| 5 | NFR-001 | 可用性 | 月間稼働率 | 0.9995 | % | 外形監視の成功率 | Must | 対応済 |
| 6 | NFR-002 | 性能 | 注文作成API P95 | 300 | ms | Gateway latency histogram | Must | 進行中 |
| 7 | NFR-003 | 災害復旧 | RPO | 5 | 分 | 復旧訓練で確認 | Must | 未対応 |
| 8 | NFR-004 | 災害復旧 | RTO | 30 | 分 | 復旧訓練で確認 | Must | 未対応 |
| 9 | NFR-005 | 性能 | 持続スループット | 250 | req/s | 負荷試験30分 | Should | 進行中 |
| 10 | NFR-006 | セキュリティ | 保存時暗号化 | 1 | 必須 | 構成監査 | Must | 対応済 |
| 11 | NFR-007 | 監査 | 監査ログ保持 | 365 | 日 | ログ基盤の保持設定 | Should | 対応済 |

<!--rtmd:partition-end id=sheet-非機能要件 baseline_nodes=66-->
<!--rtmd:document-end id=doc_76b4976d551ffd47 partitions=4-->
