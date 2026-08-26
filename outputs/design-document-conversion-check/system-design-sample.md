---
drmd_schema: 1.0
drmd_rules: 1.0
document_id: doc_76b4976d551ffd47
source_format: xlsx
roundtrip_store: system-design-sample.drmd
content_policy: visible
preserve_drmd_comments: true
---
<!--drmd:partition-begin id=sheet-概要 baseline_nodes=92-->
## 概要

<!--drmd:sheet-table range=A1:H21 source-columns=A,B,C,D,E,F,G,H source-rows=1,2,4,5,6,7,10,11,12,13,14,15,17,18,19,20,21 baseline_nodes=92 editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula-->
| 注文管理プラットフォーム システム設計書 |  |  |  |  |  |  |  |
| --- | --- | --- | --- | --- | --- | --- | --- |
| レビュー用サンプル — 複数シート、参照式、入力規則を含む設計資料 |  |  |  |  |  |  |  |
| 文書ID | ARCH-OMS-001 |  | API数 | `=COUNTA('API設計'!$A$5:$A$10)` → 6 |  |  |  |
| 対象システム | 注文管理プラットフォーム |  | DB定義行数 | `=COUNTA('DB設計'!$A$5:$A$21)` → 17 |  |  |  |
| 版 | 1.2 |  | 未対応の非機能要件 | `=COUNTIF('非機能要件'!$H$5:$H$11,"未対応")` → 2 |  |  |  |
| 更新日 | 46256 |  | 可用性目標 | `='非機能要件'!D5` → 0.9995 |  |  |  |
| アーキテクチャ構成 |  |  |  |  |  |  |  |
| レイヤー | コンポーネント | 責務 | 技術 | スケール方式 | オーナー | 可用性 | セキュリティ境界 |
| Edge | API Gateway | 認証・流量制御・ルーティング | Managed Gateway | リージョン冗長 | Platform | 99.99% | Public / OAuth2 |
| Application | Order API | 注文受付・状態遷移 | .NET 10 | 水平スケール | Order Team | 99.95% | Private subnet |
| Domain | Order Worker | 在庫引当・非同期処理 | .NET Worker | キュー長連動 | Order Team | 99.90% | Private subnet |
| Data | Order DB | 注文・明細・Outbox永続化 | PostgreSQL 17 | Multi-AZ | Data Team | 99.99% | Encrypted storage |
| 主要アーキテクチャ決定 |  |  |  |  |  |  |  |
| ADR | 決定 | 理由 | 状態 | オーナー | 決定日 | 影響 | 次回レビュー |
| ADR-001 | Outboxパターンを採用 | DB更新とイベント発行の不整合を防止 | 承認済 | Order Team | 46205 | 書込経路にOutboxテーブルを追加 | 46402 |
| ADR-002 | APIを同期受付・非同期処理に分離 | ピーク負荷と外部連携遅延を吸収 | 承認済 | Platform | 46221 | 202 Acceptedと状態照会APIが必要 | 46402 |
| ADR-003 | 注文IDはULID | 時系列ソートと分散採番を両立 | レビュー中 | Data Team | 46244 | 既存UUID連携先の互換性確認 | 46280 |

<!--drmd:partition-end id=sheet-概要 baseline_nodes=92-->
<!--drmd:partition-begin id=sheet-API設計 baseline_nodes=65-->
## API設計

<!--drmd:sheet-table range=A1:I10 source-columns=A,B,C,D,E,F,G,H,I source-rows=1,2,4,5,6,7,8,9,10 baseline_nodes=65 editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula-->
| API設計 |  |  |  |  |  |  |  |  |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 外部公開・内部利用APIの契約、認証、応答時間目標を管理 |  |  |  |  |  |  |  |  |
| API ID | Method | Path | 概要 | 認証 | Request Schema | Response | SLA (ms) | Owner |
| API-001 | POST | /v1/orders | 注文を受け付ける | OAuth2 orders.write | CreateOrderRequest | 202 OrderAccepted | 300 | Order Team |
| API-002 | GET | /v1/orders/{orderId} | 注文状態を取得 | OAuth2 orders.read | Path: orderId | 200 Order | 200 | Order Team |
| API-003 | POST | /v1/orders/{orderId}/cancel | 未出荷注文を取消 | OAuth2 orders.write | CancelOrderRequest | 202 OrderAccepted | 300 | Order Team |
| API-004 | GET | /v1/orders | 顧客別注文一覧 | OAuth2 orders.read | Query: customerId,cursor | 200 OrderPage | 400 | Order Team |
| API-005 | POST | /internal/v1/reservations | 在庫引当を要求 | mTLS | ReservationRequest | 202 ReservationAccepted | 500 | Inventory Team |
| API-006 | GET | /health/ready | 依存先を含むReady判定 | Network allowlist | None | 200/503 Health | 100 | Platform |

<!--drmd:partition-end id=sheet-API設計 baseline_nodes=65-->
<!--drmd:partition-begin id=sheet-DB設計 baseline_nodes=120-->
## DB設計

<!--drmd:sheet-table range=A1:I21 source-columns=A,B,C,D,E,F,G,H,I source-rows=1,2,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21 baseline_nodes=120 editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula-->
| DB設計 |  |  |  |  |  |  |  |  |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 注文トランザクション境界内の主要テーブル・列・索引を定義 |  |  |  |  |  |  |  |  |
| Table | Column | Type | Nullable | Key | FK | Default | Index | Description |
| orders | order_id | char(26) | NO | PK |  |  | orders_pkey | ULID形式の注文ID |
| orders | customer_id | varchar(64) | NO |  |  |  | idx_orders_customer_created | 顧客ID |
| orders | status | varchar(24) | NO |  |  | 'accepted' | idx_orders_status | 注文状態 |
| orders | currency | char(3) | NO |  |  | 'JPY' |  | ISO 4217通貨コード |
| orders | total_amount | numeric(14,2) | NO |  |  | 0 |  | 税込合計金額 |
| orders | version | integer | NO |  |  | 1 |  | 楽観ロック用 |
| orders | created_at | timestamptz | NO |  |  | now() | idx_orders_customer_created | 作成日時 |
| orders | updated_at | timestamptz | NO |  |  | now() |  | 更新日時 |
| order_items | order_id | char(26) | NO | PK | orders.order_id |  | order_items_pkey | 注文ID |
| order_items | line_no | integer | NO | PK |  |  | order_items_pkey | 明細行番号 |
| order_items | sku | varchar(64) | NO |  |  |  | idx_order_items_sku | 商品SKU |
| order_items | quantity | integer | NO |  |  | 1 |  | 数量。1以上 |
| order_items | unit_price | numeric(14,2) | NO |  |  | 0 |  | 単価 |
| outbox_events | event_id | uuid | NO | PK |  | gen_random_uuid() | outbox_events_pkey | イベントID |
| outbox_events | aggregate_id | char(26) | NO |  | orders.order_id |  | idx_outbox_pending | 注文ID |
| outbox_events | event_type | varchar(128) | NO |  |  |  |  | イベント型 |
| outbox_events | payload | jsonb | NO |  |  |  |  | イベント本文 |

<!--drmd:partition-end id=sheet-DB設計 baseline_nodes=120-->
<!--drmd:partition-begin id=sheet-非機能要件 baseline_nodes=66-->
## 非機能要件

<!--drmd:sheet-table range=A1:H11 source-columns=A,B,C,D,E,F,G,H source-rows=1,2,4,5,6,7,8,9,10,11 baseline_nodes=66 editability=cell-grid operations=replace-cell constraints=preserve-range,preserve-addresses,no-insert-delete,safe-formula-->
| 非機能要件 |  |  |  |  |  |  |  |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 可用性・性能・復旧・セキュリティ・運用の受入基準 |  |  |  |  |  |  |  |
| NFR ID | カテゴリ | メトリクス | 目標値 | 単位 | 計測方法 | 優先度 | 状態 |
| NFR-001 | 可用性 | 月間稼働率 | 0.9995 | % | 外形監視の成功率 | Must | 対応済 |
| NFR-002 | 性能 | 注文作成API P95 | 300 | ms | Gateway latency histogram | Must | 進行中 |
| NFR-003 | 災害復旧 | RPO | 5 | 分 | 復旧訓練で確認 | Must | 未対応 |
| NFR-004 | 災害復旧 | RTO | 30 | 分 | 復旧訓練で確認 | Must | 未対応 |
| NFR-005 | 性能 | 持続スループット | 250 | req/s | 負荷試験30分 | Should | 進行中 |
| NFR-006 | セキュリティ | 保存時暗号化 | 1 | 必須 | 構成監査 | Must | 対応済 |
| NFR-007 | 監査 | 監査ログ保持 | 365 | 日 | ログ基盤の保持設定 | Should | 対応済 |

<!--drmd:partition-end id=sheet-非機能要件 baseline_nodes=66-->
<!--drmd:document-end id=doc_76b4976d551ffd47 partitions=4-->
