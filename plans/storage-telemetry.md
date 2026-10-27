# Storage telemetry (WS-6)

Make storage visible: every framework and perspective table's size, what makes up that size, how
fast it grows, and which event types take the bytes. Ships with a Grafana dashboard. The 2.0 central
portal reads the same meters, so the instrument names here are public API.

## Context

Evidence from a production deployment's development environment: the event store (`wh_event_body`
plus `wh_event_store`) was 50 to 57% of each database, audit records were a quarter of one
service's body bytes, and growth from 128 GB to nearly 300 GB in a month was found by hand-written
catalog queries. None of it was visible from the framework's meters.

What exists today:

| Piece | Where | Gap |
|---|---|---|
| Meter `Whizbang.TableStatistics`: `whizbang.table.estimated_bytes`, `whizbang.queue.estimated_depth`, `whizbang.table.bloat_ratio` | `src/Whizbang.Core/Observability/TableStatisticsMetrics.cs:19,30-57` | `bloat_ratio` is not on the metrics docs page |
| Collector, every 30 seconds on every instance | `src/Whizbang.Core/Observability/TableStatisticsCollector.cs:38,51-121` | fixed cadence, no options section |
| Provider | `src/Whizbang.Data.EFCore.Postgres/PostgresTableStatisticsProvider.cs` | **sizes (`:73`) and bloat (`:45`) cover a hard-coded list of 7 tables (`:21-24`)** (defect 6): no `wh_event_body`, dedup, dead letters, lifecycle completions or any perspective table. Scan statistics (`:138`) already read every table in the schema |
| Registration | `src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs:338` | EF Core driver only; the Dapper driver registers no provider |
| Maintenance outcomes, `whizbang.maintenance.rows_affected` | documented under `Whizbang.Maintenance` in `operations/observability/metrics.md` | none; reused by the dashboard |
| Dashboards | none in the repository | no folder exists |

## Goal

1. Sizes and bloat for every `wh_` table in the schema, perspective tables included, with bounded
   cardinality.
2. Size split into heap, TOAST and indexes.
3. A sampled bytes-per-event-type view of the event store.
4. Growth over time, readable without a new instrument per rate.
5. A versioned Grafana dashboard in the repository, guarded against drift from the instrument names.

## Configuration keys

New section `Whizbang:TableStatistics`, bound like the worker sections
(`src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs:783-793` is the pattern).

| Key | Environment variable | Default | Meaning |
|---|---|---|---|
| `CollectionInterval` | `Whizbang__TableStatistics__CollectionInterval` | `00:00:30` | today's constant, made settable |
| `MaxTrackedTables` | `Whizbang__TableStatistics__MaxTrackedTables` | `500` | tables past this, smallest first, are summed under `table_name="other"` |
| `EventTypeSampleInterval` | `Whizbang__TableStatistics__EventTypeSampleInterval` | `00:15:00` | `00:00:00` disables the sample |
| `EventTypeSamplePercent` | `Whizbang__TableStatistics__EventTypeSamplePercent` | `1` | `TABLESAMPLE SYSTEM` percentage |
| `MaxEventTypes` | `Whizbang__TableStatistics__MaxEventTypes` | `50` | largest types by bytes; the rest are `event_type="other"` |

## Slices

Each slice is one red-then-green increment and its own commit.

1. **Discover tables from the catalog (defect 6).** Replace `_trackedTables` with a query over
   `pg_class` in the provider's schema: `relkind` in (`r`, `p`), name `LIKE 'wh\_%'`. Apply it to sizes
   and bloat; keep the bloat ratio's existing small-table floor. Cap at `MaxTrackedTables`.
   RED: integration tests in `tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresTableStatisticsProviderTests.cs`:
   `wh_event_body`, `wh_message_deduplication`, `wh_dead_letters` and a generated perspective table
   appear; a non-`wh_` table in the same schema does not; a table in another schema does not; the
   cap folds the smallest into `other` and the sum is preserved. Docs:
   `operations/observability/metrics.md#table-statistics`.
   **Status (2026-10-24):** done. #1488, closed by PR #1515.
2. **Provider for both drivers.** The provider uses only `NpgsqlDataSource`, so move it to
   `src/Whizbang.Data.Postgres/` and register it from the Dapper driver as well (see
   `plans/dapper-efcore-startup-parity.md`). RED: a Dapper-driver test that the collector receives
   values. Docs: same page, a note that both drivers report.
   **Status (2026-10-24):** done in PR #1515: the provider lives in
   `src/Whizbang.Data.Postgres/Observability/PostgresTableStatisticsProvider.cs` and the Dapper driver registers
   it.
3. **Heap, TOAST and index bytes.** New gauge `whizbang.table.component_bytes` with tags
   `table_name` and `component` (`heap`, `toast`, `indexes`), from `pg_relation_size`,
   `pg_relation_size(reltoastrelid)` and `pg_indexes_size`. The existing `estimated_bytes` keeps its
   meaning (total) so no dashboard breaks. RED: a test that a table with large JSONB rows reports
   non-zero TOAST, and that the three components sum to the total within one page per relation.
   **Status (2026-10-24):** done in PR #1515 (`whizbang.table.component_bytes`).
4. **Live rows.** New gauge `whizbang.table.live_rows` (`pg_class.reltuples`, the same synchronous
   source the rewrite measure uses, `src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs:1163-1170`).
   With bytes it gives bytes per row, and both give growth. RED: value after `ANALYZE` matches the
   inserted count.
   **Status (2026-10-24):** done in PR #1515 (`whizbang.table.live_rows`).
5. **Growth.** No new instrument: growth is `deriv()` or `delta()` over the gauges, which every
   Prometheus-compatible backend computes. The dashboard ships those panels. RED: the dashboard
   drift test (slice 7) checks each panel names a real instrument.
   **Status (2026-10-24):** open. The growth panels ship with the dashboard. Depends on slice 7.
6. **Bytes per event type, sampled.** DONE (#1590). Slices 1-4 DONE in #1515 (#1488); `CollectionInterval` is a bound, validated setting since #1668 (2026-10-23). The `database` tag on every table-statistics gauge and the configurable `MaxTrackedTables` cap (decisions 1 and 3) DONE in #1717 (2026-10-24). On `EventTypeSampleInterval`, one instance per window (the
   claim `IndexStatisticsMaintenanceStep` uses in `wh_unique_emission_claims`,
   `src/Whizbang.Data.EFCore.Postgres/IndexStatisticsMaintenanceStep.cs:42`) samples
   `wh_event_body TABLESAMPLE SYSTEM (EventTypeSamplePercent)` joined to `wh_event_store` for
   `event_type`, and reports per type: sampled rows, mean `pg_column_size` of `event_data` and of
   `metadata`, and an extrapolated total. Gauges `whizbang.event_store.sampled_bytes_per_event`
   (tags `event_type`, `part` = `data` or `metadata`) and `whizbang.event_store.estimated_type_bytes`
   (tag `event_type`). Type names are rendered with the runtime type-name helpers
   (`ai-docs/type-naming.md`). RED: deterministic test with `REPEATABLE` seed and 100% sample:
   two types with known sizes report the right means; `MaxEventTypes` folds the rest into `other`;
   an interval of zero never queries; a non-claimant instance never queries. Docs: metrics page,
   new section under `Whizbang.TableStatistics`.
   **Status (2026-10-24):** done. #1590, closed by PR #1591. Decision 2 (#1675) is not built: the sample reads
   only `wh_event_body`, not the `wh_event_store` pointer rows or the inline outbox metadata.
7. **Grafana dashboard.** New folder `dashboards/grafana/` with `whizbang-storage.json`: total and
   per-table bytes, component split, growth per day, bytes per row, bloat ratio with the rewrite
   threshold line, top event types by bytes, metadata share, extension states (from
   `whizbang.postgres.extension.state`, `storage-extension-capabilities.md`), and maintenance
   `rows_affected` by task. Datasource and job are template variables; no environment names.
   RED: a unit test that parses the JSON and fails when a panel's query names a metric that no meter
   in the repository declares, so a rename breaks the build rather than the dashboard. Docs: new
   `operations/observability/storage-dashboard.md` (import steps, panel meanings, the PromQL).
   **Status (2026-10-24):** open. The repository has no `dashboards/` folder.
8. **Docs catch-up.** Add `whizbang.table.bloat_ratio` (shipped, undocumented) and every new
   instrument to `operations/observability/metrics.md#table-statistics`, and the new section to
   `operations/configuration/configuration-reference.md`.
   **Status (2026-10-24):** done. The docs repo's `operations/observability/metrics.md` lists `bloat_ratio` and
   every new instrument, and the configuration reference has the `Whizbang:TableStatistics` section.
   `CollectionInterval` is a bound, validated option since #1668 (PR #1695). Decisions 1 (a `database` tag) and
   3 (a configurable cap) are done in #1723 (#1717).

## Engineering standards

- Strict red before green (`ai-docs/tdd-strict.md`); 100% of new lines and hand-written branches
  (`ai-docs/coverage-exclusions.md`).
- No reflection (`ai-docs/aot-requirements.md`); the new options bind through the configuration
  binder source generator like every other section.
- Catalog reads only on the 30-second path; anything that reads table data (the sample) runs on its
  own interval and once per fleet. Measuring guidance in `ai-docs/load-under-bulk-import.md`.
- Cardinality is bounded by configuration and tested at the bound.
- Deterministic tests: wait on the collector's `CycleCompleted` signal
  (`TableStatisticsCollector.cs:48`), never on time (`ai-docs/flaky-tests.md`).
- `<docs>` and `<tests>` tags on public types (`ai-docs/documentation-maintenance.md`).
- No migration expected; if one is needed, `src/Whizbang.Data.Postgres/Migrations/README.md` applies.

## Verification

- On the development environment, the dashboard's totals match `pg_database_size` within a few
  percent, and `wh_event_body` appears among the largest tables.
- The sampled type breakdown agrees in rank with a one-off full scan on a copied table.
- The meter's series count stays under `MaxTrackedTables + MaxEventTypes` plus a small constant.

## Decisions (formerly open questions)

All decided on 2026-10-22 in #1675 (the owner accepted the recommendations).

1. **A `database` tag** is added to the table gauges before 1.0, while the series are new. DONE (#1717): every gauge on the meter carries it.
2. **The bytes sample** also covers `wh_event_store` pointer rows and the inline outbox metadata.
3. **The table cap** stays at 500 and becomes configurable. DONE (#1717): `Whizbang:TableStatistics:MaxTrackedTables`, validated at start, applied per database.
4. **The dashboard** lives in this repository for 1.0.
