-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 539_RetiredEventTypesArePurgedByName.sql (#2329): the purge and its name resolver are dropped. Events it
-- already purged stay purged.

DROP FUNCTION IF EXISTS __SCHEMA__.wh_retire_event_types(TEXT[], INTEGER);
DROP FUNCTION IF EXISTS __SCHEMA__.wh_retired_event_type_names(TEXT[]);
