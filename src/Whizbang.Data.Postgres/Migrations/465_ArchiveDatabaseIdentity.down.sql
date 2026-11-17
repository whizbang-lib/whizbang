-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 465_ArchiveDatabaseIdentity.sql (#2367): the archive identity goes. Lossy: applying 465 again gives
-- the database a new id, so an orphan sweep of a container this database claimed is refused until an operator replaces
-- that container's owner marker.

DELETE FROM __SCHEMA__.wh_settings WHERE setting_key = 'archive_database_id';
