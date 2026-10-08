-- Migration: 195_EventStoreTypeTenantIndex.sql
-- Date: 2026-10-07
-- Description: An event-store read by event type and tenant is served by an index (#1216).
--
--              A collective replay (rebuild or rewind) asks the event store where every collective of
--              the model's types sits in the stream's tenant:
--                SELECT stream_id, event_id, commit_sequence FROM wh_event_store
--                WHERE event_type = ANY(...) AND scope ->> 't' = ...
--              The query is EF-translated (CollectiveReplayApplier.CollectivePositions) and sends the
--              JSON key as the literal 't'. wh_event_store had no index over event_type or the tenant
--              key, so each replay read the whole store to return a few rows. On a deployed service:
--              361 calls at 1.6 s each. Measured on a 4,000,000-event store: a parallel sequential scan
--              of 137,938 buffers before; an index range scan whose cost is the rows it returns after.
--
--              Keyed by event type first: the type list becomes one index range per type, the tenant
--              narrows each range inside the index, and a read by type alone (GetEventsByType) is
--              served by the same index. Both keys repeat heavily, so deduplication keeps it small:
--              31 MB on the 4,000,000-event store, against 153 MB for the primary key.
--
--              Built inside the migration's transaction, like 155: on a large store the first start
--              of this release pays one index build.
--
-- Dependencies: wh_event_store (the infrastructure schema, applied before the migrations)
-- Objects: idx_event_store_type_tenant

CREATE INDEX IF NOT EXISTS idx_event_store_type_tenant
  ON __SCHEMA__.wh_event_store (event_type, (scope ->> 't'));

COMMENT ON INDEX __SCHEMA__.idx_event_store_type_tenant IS
  'Event-store reads by event type and tenant (195): a collective replay''s position read, and reads by type alone.';
