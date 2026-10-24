-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 234_PerspectiveAppliedAutovacuum.sql
-- Date: 2026-10-22
-- Description: The applied-event ledger vacuums after a fixed number of changes rather than a fraction of the table
--              (#1666, WS-4 slice C2).
--
--              wh_perspective_applied (177) takes a row for every completed apply and its own completion call prunes
--              the rows an hour old, so the table is a sliding window over the streams: it inserts and deletes at the
--              same rate, and its size is whatever the last hour held. It stays unpartitioned (decided in #1577). A
--              scale factor makes its vacuum wait on a share of that window, which grows with the traffic, so the
--              busier the service the more dead rows the status reads walk past. The same shape as 222:
--
--                autovacuum_vacuum_scale_factor 0, threshold 10000         the prune's deletes
--                autovacuum_vacuum_insert_scale_factor 0, threshold 10000  the completions' inserts, which keep the
--                                                                          visibility map current for the status reads
--
--              The analyze scale factors are left as they are. Storage parameters only; re-applying sets the same
--              values.
--
--              Number 234: 231 and 232 are taken by other changes.
--
-- Dependencies: 177 (wh_perspective_applied)
-- Objects: reloptions of wh_perspective_applied
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

ALTER TABLE __SCHEMA__.wh_perspective_applied SET (
  autovacuum_vacuum_scale_factor = 0,
  autovacuum_vacuum_threshold = 10000,
  autovacuum_vacuum_insert_scale_factor = 0,
  autovacuum_vacuum_insert_threshold = 10000
);
