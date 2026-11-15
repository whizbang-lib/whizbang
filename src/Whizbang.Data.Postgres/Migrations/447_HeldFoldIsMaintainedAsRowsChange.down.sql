-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 447_HeldFoldIsMaintainedAsRowsChange.sql (#2351): the triggers, functions, tables and index go, and
-- the type-level held fold reads the rows again.

DROP TRIGGER IF EXISTS wh_event_store_tracks_unsequenced_insert_trg ON __SCHEMA__.wh_event_store;
DROP TRIGGER IF EXISTS wh_event_store_tracks_unsequenced_update_trg ON __SCHEMA__.wh_event_store;
DROP TRIGGER IF EXISTS wh_event_store_tracks_unsequenced_delete_trg ON __SCHEMA__.wh_event_store;
DROP TRIGGER IF EXISTS wh_event_store_clears_unsequenced_trg ON __SCHEMA__.wh_event_store;
SELECT __SCHEMA__.drop_all_overloads('wh_unsequenced_received_type_digests');
SELECT __SCHEMA__.drop_all_overloads('_wh_fold_unsequenced_digest_changes');
SELECT __SCHEMA__.drop_all_overloads('wh_rebuild_unsequenced_digests');
SELECT __SCHEMA__.drop_all_overloads('_wh_clear_unsequenced_digests');
SELECT __SCHEMA__.drop_all_overloads('_wh_track_unsequenced_digest');
DROP INDEX IF EXISTS __SCHEMA__.idx_event_store_unsequenced_received;
DROP TABLE IF EXISTS __SCHEMA__.wh_unsequenced_digest_changes;
DROP TABLE IF EXISTS __SCHEMA__.wh_unsequenced_digest_totals;
