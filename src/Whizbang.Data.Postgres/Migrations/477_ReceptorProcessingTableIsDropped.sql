-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 477_ReceptorProcessingTableIsDropped.sql
-- Date: 2026-11-17
-- Description: wh_receptor_processing is dropped.
--
--              Nothing has written the table since receptors stopped recording per-event progress, and since 475 and
--              476 nothing reads it. Migration 031 creates an index on it at the top level, and a shipped migration is
--              never edited, so the table has to exist whenever 031 runs. Owner decision (2026-11-17, option b): the
--              bootstrap creates it only until this migration has recorded its current hash
--              (RetiredReceptorTable.BootstrapSql). A database migrating from empty, or a pass that runs 031 again,
--              gets the table from the bootstrap and loses it here; once this file is recorded with its hash, the
--              bootstrap leaves the table alone and the file is skipped.
--
--              Every function that named the table was redefined without it by 475 and 476, which run before this file,
--              so no function in the schema names it once it is gone. Its indexes go with it.
--
--              Number 477: the range this bundle assigned (475 to 479). The bootstrap finds this file by its name after
--              the number (RetiredReceptorTable.DROP_MIGRATION_NAME), so a renumbering needs no other change.
--
-- Reversal: reversible (the down file re-creates the table empty, as an earlier release's bootstrap and 031 left it; it
--           held no rows anything wrote)
-- Dependencies: 031 (its index on the table), 475 and 476 (the table's last readers redefined)
-- Objects: wh_receptor_processing

DROP TABLE IF EXISTS __SCHEMA__.wh_receptor_processing;
