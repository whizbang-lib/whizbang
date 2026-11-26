-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- The reverse of 553_ArchiveLocationPinIsNormalized.sql (#2141): location rows are no longer normalized on write, and the
-- lookups and the repoint go back to their 353 definitions, which compare what they are given as written. Rows already
-- normalized stay normalized, and merged rows stay merged: no record loses its location.

DROP TRIGGER IF EXISTS wh_archive_locations_normalize_trg ON __SCHEMA__.wh_archive_locations;
DROP FUNCTION IF EXISTS __SCHEMA__._wh_archive_locations_normalize();
DROP FUNCTION IF EXISTS __SCHEMA__.wh_normalize_archive_locations();

-- <docs>fundamentals/events/archive-store#archive-locations</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:Backfill_SplitsEveryLegacyLocator_IntoALocationPerContainerAndARelativePath_AndRunsOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:OlderInstanceWrite_WithOnlyALocator_IsGivenALocationAndPathAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_archive_legacy_location_id(p_provider TEXT, p_container TEXT) RETURNS BIGINT AS $$
DECLARE
  v_id BIGINT;
BEGIN
  INSERT INTO __SCHEMA__.wh_archive_locations (provider, container) VALUES (p_provider, p_container)
  ON CONFLICT DO NOTHING RETURNING location_id INTO v_id;
  IF v_id IS NULL THEN
    SELECT location_id INTO v_id FROM __SCHEMA__.wh_archive_locations
    WHERE provider = p_provider AND account_endpoint IS NULL AND connection_name IS NULL AND container = p_container;
  END IF;
  RETURN v_id;
END;
$$ LANGUAGE plpgsql;

-- <docs>fundamentals/events/archive-store#archive-locations</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:LocationId_AdoptsItsContainersLegacyLocation_ThenFindsIt_AndALookupNeverCreatesOneAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:LocationId_ALegacyLocationBesideTheConfiguredOne_IsMergedIntoItAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:LocationId_FindsALocationWrittenAnotherWay_AndRepointStoresItNormalizedAsync</tests>
-- <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:Write_RecordsTheConfiguredLocation_AndOnlyAPathFromTheStreamOnEachObject_NeverASecretAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_archive_location_id(
  p_provider TEXT,
  p_account_endpoint TEXT,
  p_connection_name TEXT,
  p_container TEXT,
  p_create BOOLEAN
) RETURNS BIGINT AS $$
DECLARE
  v_id BIGINT;
  v_legacy BIGINT;
BEGIN
  SELECT location_id INTO v_id FROM __SCHEMA__.wh_archive_locations
  WHERE provider = p_provider AND account_endpoint = p_account_endpoint AND connection_name = p_connection_name
    AND container = p_container;

  -- A container's records written before endpoints were recorded are taken to be at the configured endpoint: the
  -- database never knew another. Records of any other container keep failing the check.
  SELECT location_id INTO v_legacy FROM __SCHEMA__.wh_archive_locations
  WHERE provider = p_provider AND account_endpoint IS NULL AND connection_name IS NULL AND container = p_container
  FOR UPDATE;
  IF v_legacy IS NOT NULL THEN
    IF v_id IS NULL THEN
      UPDATE __SCHEMA__.wh_archive_locations
      SET account_endpoint = p_account_endpoint, connection_name = p_connection_name
      WHERE location_id = v_legacy;
      RETURN v_legacy;
    END IF;
    PERFORM __SCHEMA__._wh_merge_archive_location(v_legacy, v_id);
  END IF;

  IF v_id IS NULL AND p_create THEN
    INSERT INTO __SCHEMA__.wh_archive_locations (provider, account_endpoint, connection_name, container)
    VALUES (p_provider, p_account_endpoint, p_connection_name, p_container)
    ON CONFLICT DO NOTHING
    RETURNING location_id INTO v_id;
    IF v_id IS NULL THEN
      SELECT location_id INTO v_id FROM __SCHEMA__.wh_archive_locations
      WHERE provider = p_provider AND account_endpoint = p_account_endpoint AND connection_name = p_connection_name
        AND container = p_container;
    END IF;
  END IF;
  RETURN v_id;
END;
$$ LANGUAGE plpgsql;

-- <docs>fundamentals/events/archive-store#repointing-an-environment</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:Repoint_UpdatesTheOneRow_OrMergesItIntoTheConfiguredLocationAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:LocationId_FindsALocationWrittenAnotherWay_AndRepointStoresItNormalizedAsync</tests>
-- <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:RepointLocation_MakesAMismatchedDatabaseConsistent_ForAnotherContainerAsync</tests>
-- <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreTests.cs:RepointLocation_AfterTheCopyArchivedItsOwnStreams_MergesTheSourceLocationAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_repoint_archive_location(
  p_from_location_id BIGINT,
  p_provider TEXT,
  p_account_endpoint TEXT,
  p_connection_name TEXT,
  p_container TEXT
) RETURNS BIGINT AS $$
DECLARE
  v_to BIGINT;
BEGIN
  SELECT location_id INTO v_to FROM __SCHEMA__.wh_archive_locations
  WHERE provider = p_provider AND account_endpoint = p_account_endpoint AND connection_name = p_connection_name
    AND container = p_container;
  IF v_to IS NULL THEN
    -- The usual case: the one row changes, and every record of the location follows it.
    UPDATE __SCHEMA__.wh_archive_locations
    SET provider = p_provider, account_endpoint = p_account_endpoint, connection_name = p_connection_name,
        container = p_container
    WHERE location_id = p_from_location_id
    RETURNING location_id INTO v_to;
  ELSIF v_to <> p_from_location_id AND EXISTS (
      SELECT 1 FROM __SCHEMA__.wh_archive_locations WHERE location_id = p_from_location_id) THEN
    -- The configured location already exists (the service archived since the move): the records join it.
    PERFORM __SCHEMA__._wh_merge_archive_location(p_from_location_id, v_to);
  END IF;
  RETURN v_to;
END;
$$ LANGUAGE plpgsql;

DROP FUNCTION IF EXISTS __SCHEMA__._wh_normalize_archive_endpoint(TEXT);
DROP FUNCTION IF EXISTS __SCHEMA__._wh_normalize_archive_name(TEXT);
