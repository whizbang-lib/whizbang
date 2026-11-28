-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Down migration: 750_StoredVectorHistoryIsPackedAndCanBeUnpackedForADowngrade.down.sql
-- Restores the packing gate to its last word before 750 (720) and drops the history pass, the unpack, the operator's
-- commands and the request table. Bodies keep whatever form they hold. A journal row left under the unpacking name by
-- an unfinished unpack is renamed back, so the 720 gate and the write paths see the conversion they journaled; the
-- record of packed paths stays, so the reader guard keeps refusing a build that no longer reads one.
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

UPDATE __SCHEMA__.wh_stored_form_migrations SET name = 'wh_event_body:packed-vectors'
WHERE name = 'wh_event_body:packed-vectors-unpacking'
  AND NOT EXISTS (SELECT 1 FROM __SCHEMA__.wh_stored_form_migrations j WHERE j.name = 'wh_event_body:packed-vectors');

SELECT __SCHEMA__.drop_all_overloads('wh_open_vector_packing_gate');
-- Opens the packing gate once every live instance reads packed vectors: at least one instance is live (the one liveness
-- rule), and each live instance's registry row says stored_form_level 2 or higher. Opening journals the conversion,
-- applied by p_library_version (an unknown version keeps the gate closed: an unjournaled conversion could not stand an
-- older library down). One-way. Returns 'open' (already), 'opened' (this call), or 'closed (...)'.
-- <docs>data/event-store#packing-gate</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_AFreshService_OpensOnlyOnceThisInstanceHasRegisteredWithItsMarkerAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_StaysClosedWhileALiveInstanceReadsNoPackedVectors_AndOpensOnceItIsGoneStaleOrRaisedAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_OnceOpen_StaysOpen_WhenAnInstanceWithoutTheMarkerAppearsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_WithoutALibraryVersion_StaysClosed_SoNothingPacksUnjournaledAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Journal_RecordsTheLibraryThatStartedPacking_AndNoApplicationVersionAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Rollback_AnOlderLibrary_StandsDownAtAssess_NamingPackedVectorsAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorReaderGuardSqlTests.cs:Ledger_RecordsTheRegisteredPaths_OnlyOnceTheGateOpens_AndKeepsThemWhenARegistrationDropsThemAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_open_vector_packing_gate(p_library_version TEXT)
RETURNS TEXT AS $$
DECLARE
  v_live BIGINT;
  v_ready BIGINT;
  v_opened BOOLEAN;
BEGIN
  IF __SCHEMA__._wh_vector_packing_on() THEN
    RETURN 'open';
  END IF;
  IF COALESCE(btrim(p_library_version), '') = '' THEN
    RETURN 'closed (this instance''s library version is unknown, so the conversion cannot be journaled)';
  END IF;

  SELECT count(*),
         count(*) FILTER (WHERE jsonb_typeof(si.metadata -> 'stored_form_level') = 'number'
                            AND (si.metadata ->> 'stored_form_level')::NUMERIC >= 2)
    INTO v_live, v_ready
  FROM __SCHEMA__.wh_service_instances si
  WHERE si.instance_id IN (SELECT l.instance_id FROM __SCHEMA__.wh_live_instance_ids() l);

  IF v_live = 0 OR v_ready < v_live THEN
    RETURN 'closed (' || v_ready || ' of ' || v_live || ' live instances read packed vectors)';
  END IF;

  WITH journaled AS (
    INSERT INTO __SCHEMA__.wh_stored_form_migrations
      (name, table_name, kind, declared_at, first_applied_at, last_applied_at, rows_converted, applied_library_version)
    VALUES ('wh_event_body:packed-vectors', 'wh_event_body', 'library', NOW(), NOW(), NOW(), 0, p_library_version)
    ON CONFLICT (name) DO UPDATE
      SET applied_library_version = EXCLUDED.applied_library_version,
          first_applied_at = COALESCE(__SCHEMA__.wh_stored_form_migrations.first_applied_at, NOW()),
          last_applied_at = NOW()
      WHERE __SCHEMA__.wh_stored_form_migrations.applied_library_version IS NULL
    RETURNING 1
  ), recorded AS (
    -- The paths registered so far are recorded as packed in the statement that opens the gate.
    INSERT INTO __SCHEMA__.wh_packed_vector_paths (event_type, path)
    SELECT vp.event_type, p.path
    FROM __SCHEMA__.wh_event_vector_paths vp
    CROSS JOIN LATERAL jsonb_array_elements(vp.paths) AS p(path)
    WHERE EXISTS (SELECT 1 FROM journaled)
    ON CONFLICT DO NOTHING
    RETURNING 1
  )
  SELECT EXISTS (SELECT 1 FROM journaled) INTO v_opened;

  RETURN CASE WHEN v_opened THEN 'opened' ELSE 'open' END;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_open_vector_packing_gate(TEXT) IS
  'Issue #2625: journals the packed-vectors conversion (wh_stored_form_migrations wh_event_body:packed-vectors, applied '
  'by p_library_version), which opens the write paths'' packing, once at least one instance is live and every live '
  'instance''s registry metadata says stored_form_level >= 2. One-way. Returns open, opened, or closed (...). Called by '
  'the vector-packing-gate maintenance step.';

SELECT __SCHEMA__.drop_all_overloads('wh_resume_vector_packing');
SELECT __SCHEMA__.drop_all_overloads('wh_request_vector_unpack');
SELECT __SCHEMA__.drop_all_overloads('wh_vector_unpack_finish');
SELECT __SCHEMA__.drop_all_overloads('wh_vector_archive_unpack_batch');
SELECT __SCHEMA__.drop_all_overloads('wh_vector_history_unpack_apply');
SELECT __SCHEMA__.drop_all_overloads('wh_vector_history_pack_apply');
SELECT __SCHEMA__.drop_all_overloads('wh_vector_conversion_mode');
SELECT __SCHEMA__.drop_all_overloads('_wh_vector_packing_lock');
SELECT __SCHEMA__.drop_all_overloads('_wh_unpack_vectors');
DELETE FROM __SCHEMA__.wh_type_history_walks WHERE operation IN ('pack-vectors', 'unpack-vectors');
DROP TABLE IF EXISTS __SCHEMA__.wh_vector_unpack_request;
