# Event store type and scope strings as ids (#2324)

Status: designed and measured; not implemented. This plan is the hand-off for the implementation PR.

## Problem

`wh_event_store` (the pointer table) repeats three wide strings with very few distinct values in every row, and
three of its largest indexes key on them. Measured on one long-running database (read-only statistics):

| Column | Average width | Distinct values |
|---|---|---|
| `scope` (jsonb) | 112 B | about 290 |
| `event_type` (stored form, `Namespace.Type, Assembly`) | 79 B | about 228 |
| `aggregate_type` | 64 B | about 228 |

| Index | Size | Scans |
|---|---|---|
| `idx_event_store_type_commit_sequence (event_type, commit_sequence)` | 1047 MB | 186,706 |
| `idx_event_store_aggregate_type (aggregate_type, created_at)` | 1025 MB | 13,810 |
| `idx_event_store_type_tenant (event_type, scope ->> 't')` | 134 MB (deduplicated) | 2,955,422 |
| heap | 3143 MB for about 8.1 million rows | |

`wh_stream_digests` (#2325) has the same shape: its primary key `(origin_service_id, scope_tenant, event_type,
stream_id)` is 1 GB on 3.1 million rows, `event_type` being 84 of its roughly 150 key bytes.

## Measurement on a copy

A scratch PostgreSQL 17 database (not any deployed database), 2,000,000 synthetic pointer rows with the measured
widths and cardinalities (228 type names of about 79 characters, 228 aggregate type names, 290 scope documents over 39
tenants), the three type-keyed indexes built on each form, `VACUUM ANALYZE` before measuring. Script:
`measure2324.sql` (kept with the PR notes, not committed).

| Relation | Strings | Ids | Change |
|---|---|---|---|
| heap | 651 MB | 240 MB | -63% |
| average row (`pg_column_size`) | 327 B | 120 B | -207 B |
| `(event_type, commit_sequence)` | 218 MB | 60 MB | -72% |
| `(aggregate_type, created_at)` | 165 MB | 60 MB | -64% |
| `(event_type, tenant)` | 17 MB | 14 MB | -18% (already deduplicated) |

A type-filtered read through the lookup (`event_type_id = (SELECT id FROM names WHERE name = $1)`) used the id index
with an index-only scan and read 37 buffers where the string form read 2,888 for the same 4,386 rows. Scaled to the
measured database: about 1.6 GB of heap and about 1.4 GB of index on that one database, before `wh_stream_digests`.

## Design

### Lookup tables

- **`wh_event_type_names (id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name TEXT NOT NULL UNIQUE)`**, one row
  per distinct stored string, used for both `event_type` and `aggregate_type`. `wh_message_type_registry` does not fit:
  its id is a 16-byte uuid, it is keyed by the CLR name without the assembly while the store keys by the stored form,
  and the store holds names the registry never sees (former names after a rename, types of deleted code, names from
  other services). The dictionary is insert-only: a name is never renamed in place, so an id is stable forever.
- **`wh_scope_dict (id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY, scope JSONB NOT NULL UNIQUE, tenant TEXT
  GENERATED ALWAYS AS (scope ->> 't') STORED)`**. Exact-document identity: jsonb equality is canonical (key order and
  whitespace do not matter), so two writers storing the same scope share one id.
- One helper per dictionary, `_wh_type_name_id(TEXT) RETURNS INTEGER` and `_wh_scope_id(JSONB) RETURNS INTEGER`:
  `INSERT ... ON CONFLICT DO NOTHING` then `SELECT`, so concurrent writers converge without locks beyond the unique
  index. Both are `STABLE` from the reader's side (a name that is absent returns NULL to a reader that does not insert).

### Columns

Add `event_type_id INTEGER`, `aggregate_type_id INTEGER`, `scope_id INTEGER` to `wh_event_store` (nullable during the
migration). The string columns stay until every reader is moved and every row is backfilled; dropping them is the
last migration of the series, and the reclaimed space arrives with the next table rewrite (the online rewrite machinery
of #2319, never `VACUUM FULL`).

### Every reader and writer

From the repository at 34d0a2ba8: 103 migration files touch `wh_event_store` together with `event_type`, 38 with
`aggregate_type`, 93 with `scope`; 48 C# files in the three Postgres data projects read an event type. Only the latest
definition of each function matters (rule 5); the implementation PR enumerates them with
`scripts/Lint-MigrationSql.ps1`'s function index rather than by grep (an enumeration that fails silently toward too few
is the risk here). The classes:

1. **Write paths** (`wh_append_event`, the emit chains `store_outbox_messages` and `commit_handler_result`, the
   received-event path, the archive restore): resolve the three ids once per batch through the helpers and write both
   forms while the series is in flight, the ids only after the last step.
2. **Filters by type name** (the per-type history walker `wh_type_history_begin/advance`, the ephemeral reaper's
   `wh_ephemeral_type_grace` join, superseding, the mixed-stream guard, `reclassify_events_ephemeral`, the audit
   ledger rebuild, the stream end-of-life policies, the claim's type filters): `es.event_type = $1` becomes
   `es.event_type_id = _wh_type_name_id_or_null($1)`; joins on `event_type` (grace, associations, allowances) join
   through the dictionary or carry the id in their own table.
3. **Projections that return the name** (event store reads to C#, perspective claims, rewind, rebuild, archive):
   `JOIN wh_event_type_names` on the id; the C# `EFCoreEventStore` and Dapper readers keep returning the string, so
   no C# reader changes shape. EF Core maps the new columns as shadow properties.
4. **Tenant filters** (`scope ->> 't'`, `idx_event_store_type_tenant`): `scope_id IN (SELECT id FROM wh_scope_dict
   WHERE tenant = $1)`, or a denormalized `tenant_id` when the planner needs a single-column key.
5. **`wh_stream_digests`**: its key column `event_type` becomes `event_type_id`, folded through `_wh_digest_fold` (one
   helper since 336), with the same online backfill.

### Indexes

Built `CONCURRENTLY` outside the migration transaction (the startup step that builds deferred indexes, as the
perspective schema pass does), each beside the string index it replaces; the string index is dropped only after the
reader switch has shipped:

- `(event_type_id, commit_sequence) WHERE commit_sequence IS NOT NULL`
- `(aggregate_type_id, created_at)`
- `(event_type_id, scope_id)` for the tenant filter
- `idx_event_store_ephemeral_reap` keeps its `INCLUDE` with `event_type_id` instead of `event_type`.

### Online backfill

A maintenance step on the per-type history walker's shape: bounded windows by `event_id` (the primary key), each its
own short transaction, `UPDATE ... SET event_type_id = ..., aggregate_type_id = ..., scope_id = ... WHERE event_id
BETWEEN ... AND <id> IS NULL`, rows taken `FOR UPDATE SKIP LOCKED` so it never waits on an append and an append never
waits on it beyond one window; a cursor in its own one-row table; a remaining-rows count through
`wh_pending_reclamation` (`backfill_event_store_type_ids`), as the history conversions do. Appends write the ids from
the first migration on, so the backfill only ever moves backwards over history and finishes. Each window updates one
fixed number of rows, so its WAL and dead tuples are bounded; HOT updates do not apply (indexed columns), which the
fillfactor work of #2319 accounts for.

### Order of the series

1. Dictionaries, helpers, nullable columns; write paths write both forms. (No reader changes.)
2. Backfill step; deferred id indexes built concurrently.
3. Readers switch to ids, gated on the backfill being complete (a reader falls back to the string predicate while
   `wh_settings.event_store_type_ids_backfilled` is not set, so a partially backfilled store never returns too few
   rows).
4. String indexes dropped; later, string columns dropped and the space reclaimed by the online rewrite.

Each step is its own PR with its own migration, and steps 1 to 3 are each safe to ship and to stop after.

## Tests the implementation needs

- Every read path that filters by event type, aggregate type or tenant returns the same rows before and after the
  switch, over a fixture with renamed types, unknown types, NULL scope keys and mixed-key-order scope documents.
- The backfill converts in bounded windows, resumes after a crash, never blocks a concurrent append (a held row is
  passed over and taken next cycle), and reports remaining rows reaching zero.
- Index and row size reduction measured on a representative copy (the script above, at scale).

## Why not in the storage bundle

It changes every write path and roughly a hundred function definitions, and a reader switched before its rows are
backfilled returns too few rows silently. The bundle ships the measurement and this plan instead of a half migration.
