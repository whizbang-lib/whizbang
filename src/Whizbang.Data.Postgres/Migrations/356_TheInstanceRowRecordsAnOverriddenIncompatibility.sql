-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 356_TheInstanceRowRecordsAnOverriddenIncompatibility.sql
-- Date: 2026-11-07
-- Description: An instance started past an incompatibility verdict says so on its row (#2022).
--
--              Whizbang:Startup:OverrideIncompatibility lets an instance start when startup's verdict would stand it
--              down for an incompatibility: the schema newer than its build, or a change it cannot reverse applied since
--              its version. Such an instance serves a schema it did not create, and operators and its peers must be able
--              to see that without reading its logs. wh_service_instances.incompatibility_override holds the reason the
--              verdict would have stood it down, written once the instance registers; NULL on every other instance.
--
--              The instance's own start writes it (SchemaBootstrapPhase.RecordIncompatibilityOverrideAsync). Nothing else
--              writes it, and the heartbeat leaves it alone.
--
--              Rolling deploy: an older release neither reads nor writes the column.
--
--              Number 356: 349 to 353 are on develop or in flight, and 355 is taken by a change in flight.
--
-- Dependencies: the core infrastructure schema (wh_service_instances), 316 (the one instance record)
-- Objects: wh_service_instances.incompatibility_override
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README
--            rule 12).

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_attribute
                 WHERE attrelid = to_regclass('__SCHEMA__.wh_service_instances')
                   AND attname = 'incompatibility_override'
                   AND NOT attisdropped) THEN
    ALTER TABLE __SCHEMA__.wh_service_instances ADD COLUMN IF NOT EXISTS incompatibility_override TEXT;

    COMMENT ON COLUMN __SCHEMA__.wh_service_instances.incompatibility_override IS
      '356 (#2022): set when Whizbang:Startup:OverrideIncompatibility let this instance start past an incompatibility '
      'verdict: the reason the verdict would have stood it down. NULL otherwise.';
  END IF;
END $$;
