-- Migration: 200_ManagedObjects.sql
-- Date: 2026-10-08
-- Description: The ledger of database objects Whizbang manages for perspectives, and the functions a DBA
--              pins and unpins them with (#1252).
--
--              Whizbang creates indexes, constraints, triggers and columns from what perspective models
--              declare, and until now never removed one: an index a model stopped declaring stayed in every
--              database, maintained on every write. To remove what it created, Whizbang has to know what it
--              created. Each row here records one object on a perspective table: its kind, its definition,
--              who owns it (whizbang, or foreign: created by someone else and never dropped), its status
--              (active, pending-retirement, retired) and whether it is pinned.
--
--              A pinned object is never dropped, whatever the model or the settings say. An object has two
--              independent pins. The code pin (code_*) is controlled by C#: [KeepSchemaObject], a code
--              registration or configuration sets it at every start, and removing the declaration releases it.
--              The database pin (db_*) is set with these functions, the CLI or a 'whizbang:pin' comment, and
--              only an unpin here or in the CLI releases it; startup never changes it.
--
--              wh_managed_object_declarations records, per running instance, every object it declares, so an
--              object is dropped only once no running instance still declares it: under a rolling update the
--              previous release keeps querying its indexes until the new one is ready.
--
-- Dependencies: 000 (drop_all_overloads)
-- Objects: wh_managed_objects, wh_managed_object_declarations, wh_pin_object, wh_unpin_object

CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_managed_objects (
  table_name        TEXT        NOT NULL,
  object_name       TEXT        NOT NULL,
  object_kind       TEXT        NOT NULL DEFAULT 'unknown',
  definition        TEXT,
  owner             TEXT,
  status            TEXT        NOT NULL DEFAULT 'unclassified',
  declared_by       TEXT,
  undeclared_since  TIMESTAMPTZ,
  first_recorded_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  last_seen_at      TIMESTAMPTZ,
  retired_at        TIMESTAMPTZ,
  code_pinned       BOOLEAN     NOT NULL DEFAULT FALSE,
  code_pin_source   TEXT,
  code_pin_reason   TEXT,
  db_pinned         BOOLEAN     NOT NULL DEFAULT FALSE,
  db_pin_source     TEXT,
  db_pin_reason     TEXT,
  db_pinned_by      TEXT,
  db_pinned_at      TIMESTAMPTZ,
  PRIMARY KEY (table_name, object_name)
);

COMMENT ON TABLE __SCHEMA__.wh_managed_objects IS
  'The database objects Whizbang manages for perspective tables: created from what the model declares, dropped when '
  'Whizbang created them and the model no longer declares them, never dropped when foreign or pinned (#1252).';

-- What each running instance declares, as table:object, replaced at each of its reconciles.
-- <docs>fundamentals/perspectives/managed-schema-objects#settings</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaReconcilerTests.cs:AnObjectARunningInstanceStillDeclares_IsKeptUntilItStopsAsync</tests>
CREATE TABLE IF NOT EXISTS __SCHEMA__.wh_managed_object_declarations (
  instance_id UUID NOT NULL PRIMARY KEY,
  objects TEXT[] NOT NULL,
  reported_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE __SCHEMA__.wh_managed_object_declarations IS
  'The objects each running instance declares (table:object), so the managed-object reconcile drops an object only '
  'once no running instance still declares it. Rows of instances no longer running are removed by the reconcile.';

SELECT __SCHEMA__.drop_all_overloads('wh_pin_object');

-- <docs>fundamentals/perspectives/managed-schema-objects</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedObjectsLedgerTests.cs:Pin_RecordsThePinWithItsReasonAndWhoSetItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedObjectsLedgerTests.cs:Pin_AnObjectTheLedgerAlreadyHas_KeepsItsClassificationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedObjectsLedgerTests.cs:Pin_InTheDatabase_LeavesTheCodePinAloneAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_pin_object(p_table TEXT, p_object TEXT, p_reason TEXT)
RETURNS TEXT AS $$
BEGIN
  INSERT INTO __SCHEMA__.wh_managed_objects AS m (table_name, object_name, db_pinned, db_pin_source, db_pinned_by, db_pinned_at, db_pin_reason)
  VALUES (p_table, p_object, TRUE, 'sql', current_user, NOW(), p_reason)
  ON CONFLICT (table_name, object_name) DO UPDATE
    SET db_pinned = TRUE, db_pin_source = 'sql', db_pinned_by = current_user, db_pinned_at = NOW(), db_pin_reason = p_reason;
  RETURN 'pinned';
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_pin_object(TEXT, TEXT, TEXT) IS
  'Sets the database pin on a managed object so Whizbang never drops it (source sql, by the current user). '
  'Startup never changes a database pin. Returns pinned.';

SELECT __SCHEMA__.drop_all_overloads('wh_unpin_object');

-- <docs>fundamentals/perspectives/managed-schema-objects</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedObjectsLedgerTests.cs:Unpin_ReleasesAPinSetInTheDatabaseAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedObjectsLedgerTests.cs:Unpin_ReleasesOnlyTheDatabasePin_AndSaysCSharpStillPinsItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedObjectsLedgerTests.cs:Unpin_AnObjectNotInTheLedger_IsAbsentAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_unpin_object(p_table TEXT, p_object TEXT)
RETURNS TEXT AS $$
DECLARE
  v_code_pinned BOOLEAN;
  v_code_source TEXT;
  v_code_reason TEXT;
BEGIN
  UPDATE __SCHEMA__.wh_managed_objects
  SET db_pinned = FALSE, db_pin_source = NULL, db_pin_reason = NULL, db_pinned_by = NULL, db_pinned_at = NULL
  WHERE table_name = p_table AND object_name = p_object
  RETURNING code_pinned, code_pin_source, code_pin_reason INTO v_code_pinned, v_code_source, v_code_reason;

  IF NOT FOUND THEN
    RETURN 'absent';
  END IF;

  -- The code pin is C#'s: it is set again at every start while the declaration exists, so say who holds it.
  IF v_code_pinned THEN
    RETURN 'unpinned; still pinned by ' || v_code_source || coalesce(': ' || v_code_reason, '');
  END IF;
  RETURN 'unpinned';
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_unpin_object(TEXT, TEXT) IS
  'Releases the database pin on a managed object. Returns unpinned, absent, or unpinned; still pinned by <source> '
  'when C# also pins it (remove the declaration to release that pin).';
