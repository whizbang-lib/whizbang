-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 561_UnpositionedReceivedHistoryAsksItsOriginForPositions.sql (#2544): the walk, the unknown record, their
-- triggers and the functions go. Positions already filled stay, which is correct: each is the origin's own.

DROP TRIGGER IF EXISTS wh_event_store_forgets_position_unknown_delete_trg ON __SCHEMA__.wh_event_store;
DROP TRIGGER IF EXISTS wh_event_store_forgets_position_unknown_update_trg ON __SCHEMA__.wh_event_store;
DROP TRIGGER IF EXISTS wh_event_store_clears_position_unknown_trg ON __SCHEMA__.wh_event_store;

DROP FUNCTION IF EXISTS __SCHEMA__.wh_origin_positions_unfilled();
DROP FUNCTION IF EXISTS __SCHEMA__.wh_fill_origin_positions(UUID, UUID[], BIGINT[], UUID[], UUID[], TIMESTAMPTZ);
DROP FUNCTION IF EXISTS __SCHEMA__._wh_rewind_streams_from_start(UUID[], TIMESTAMPTZ);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_origin_position_fill_plan(UUID, UUID[]);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_origin_positions_of(UUID[]);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_origin_position_ask(INTEGER, INTERVAL, TIMESTAMPTZ);
DROP FUNCTION IF EXISTS __SCHEMA__._wh_clear_origin_position_unknown();
DROP FUNCTION IF EXISTS __SCHEMA__._wh_forget_origin_position_unknown();

DROP TABLE IF EXISTS __SCHEMA__.wh_origin_position_unknown;
DROP TABLE IF EXISTS __SCHEMA__.wh_origin_position_walk;
