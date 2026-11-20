-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 533_RowsPurgedBeforeMarkersGetTheirMarker.sql (#2314): the backfill and its count go. Markers already
-- written stay, and keep their rows purged as the runner would have.

SELECT __SCHEMA__.drop_all_overloads('wh_backfill_purge_markers');
SELECT __SCHEMA__.drop_all_overloads('wh_count_unmarked_rowless_pairs');
