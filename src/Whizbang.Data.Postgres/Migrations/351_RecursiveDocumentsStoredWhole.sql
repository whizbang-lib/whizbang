-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 351_RecursiveDocumentsStoredWhole.sql
-- Date: 2026-11-07
-- Description: Marks a database as migrated by a release that stores a perspective model holding a recursive type as
--              one whole document (#2112).
--
--              Before that release the Entity Framework driver mapped such a model (a tree node with a collection of
--              itself, or two types holding each other) and kept only the first level of each recursive member: every
--              level below it was dropped on write, with no error. Rows those releases wrote may be truncated, and only
--              re-projecting the perspective from its events restores them. Nothing rebuilds automatically.
--
--              This file changes nothing. Its ledger row is the fact the startup notice reads
--              (RecursiveDocumentRebuildNotice): the notice runs before a start's migrations apply, so a ledger without
--              this row was last migrated by a release before the fix, and each perspective whose model is recursive
--              and whose table holds rows is named at Warning to be rebuilt. A fresh database and one already at or
--              after this release are silent. The statement below exists only so the file is not empty.
--
--              Number 351: the next free number after the ones taken on develop and in open pull requests.
--
-- Dependencies: 000 (the migration ledger)
-- Objects: none
-- Constants: none

SELECT 1;
