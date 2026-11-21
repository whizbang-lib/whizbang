-- Copyright (c) whizbang-lib contributors.
-- SPDX-License-Identifier: MIT

-- Migration: 553_ArchiveLocationPinIsNormalized.sql
-- Date: 2026-11-21
-- Description: The archive location every record references (353, #2137) is stored in its normalized form, and a
--              location written another way is the same location (#2141).
--
--              The database keeps the location pin; infrastructure decides where the objects actually are, and the
--              store's Whizbang:Archive:AzureBlob:LocationMismatch setting decides what a record pinned elsewhere does
--              (Error, the default, stops the archive; Warning and Info log and go on in the configured location;
--              Ignore goes on without comparing). For that comparison to mean "another place", a difference only in
--              how a location is written must not count. Normalized means:
--                - every part: surrounding spaces, tabs, carriage returns and line feeds removed, and lower case
--                  (DNS host names, storage account names and container names are case-insensitive or lower case
--                  only, and a logical connection name is compared without case);
--                - the endpoint: trailing slashes removed, and the scheme's default port removed when written out
--                  (:443 for https, :80 for http).
--              Everything else stays a difference: the scheme, the host (another account, the account's secondary
--              endpoint, a custom domain), any other port, the path, the connection name, the container. The library
--              applies the same rules (ArchiveLocation.Normalize); a test runs both over the same cases.
--
--              The lookups (wh_archive_location_id, _wh_archive_legacy_location_id) and the repoint
--              (wh_repoint_archive_location) normalize what they are given, so an instance of an older release, which
--              passes its configuration as written, finds the same row during a rolling deploy. A trigger normalizes
--              every row written to wh_archive_locations, the operator's own insert or update included (the
--              documented repoint by hand). Existing rows are normalized once here (wh_normalize_archive_locations):
--              rows that turn out to be one location are merged (their records and queued deletions move to the
--              surviving row), so every record written before this migration keeps naming the location it meant. No
--              record's path changes and no object is moved or deleted.
--
-- Dependencies: 353 (wh_archive_locations, _wh_merge_archive_location, wh_archive_location_id,
--               wh_repoint_archive_location, _wh_archive_legacy_location_id)
-- Objects: _wh_normalize_archive_endpoint, _wh_normalize_archive_name, _wh_archive_locations_normalize,
--          wh_archive_locations_normalize_trg, wh_normalize_archive_locations, _wh_archive_legacy_location_id,
--          wh_archive_location_id, wh_repoint_archive_location
-- Reversal: reversible (the normalization functions and trigger are dropped and the 353 lookups restored; rows already
--           normalized or merged stay so, and every record keeps its location)
-- Constants: the double-underscore tokens in this file (for example __SCHEMA__) are substituted at apply time (README rule 12).

-- <docs>fundamentals/events/archive-store#archive-locations</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:Normalize_InSql_IsTheLibrarysNormalization_ForEveryCaseAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_normalize_archive_name(p_name TEXT) RETURNS TEXT AS $$
  SELECT lower(btrim(p_name, E' \t\r\n'));
$$ LANGUAGE sql IMMUTABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_normalize_archive_name(TEXT) IS
'553 (#2141): a provider, logical connection name or container in normalized form: surrounding whitespace removed, lower case. NULL stays NULL.';

-- <docs>fundamentals/events/archive-store#archive-locations</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:Normalize_InSql_IsTheLibrarysNormalization_ForEveryCaseAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_normalize_archive_endpoint(p_endpoint TEXT) RETURNS TEXT AS $$
  SELECT regexp_replace(
           regexp_replace(rtrim(lower(btrim(p_endpoint, E' \t\r\n')), '/'), '^(https://[^/]*):443(/|$)', '\1\2'),
           '^(http://[^/]*):80(/|$)', '\1\2');
$$ LANGUAGE sql IMMUTABLE;

COMMENT ON FUNCTION __SCHEMA__._wh_normalize_archive_endpoint(TEXT) IS
'553 (#2141): a storage endpoint in normalized form: surrounding whitespace removed, lower case, no trailing slash, and no :443 (https) or :80 (http). Any other difference stays. NULL stays NULL.';

-- <docs>fundamentals/events/archive-store#archive-locations</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:Locations_AreStoredNormalized_WhateverWritesThemAsync</tests>
-- <tests>tests/Whizbang.Archive.AzureBlob.Integration.Tests/AzureBlobArchiveStoreLocationMismatchTests.cs:Failover_RecordedByHandInAnotherForm_IsTheSameLocation_UnderEveryHandlingAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_archive_locations_normalize() RETURNS TRIGGER AS $$
BEGIN
  NEW.provider := __SCHEMA__._wh_normalize_archive_name(NEW.provider);
  NEW.account_endpoint := __SCHEMA__._wh_normalize_archive_endpoint(NEW.account_endpoint);
  NEW.connection_name := __SCHEMA__._wh_normalize_archive_name(NEW.connection_name);
  NEW.container := __SCHEMA__._wh_normalize_archive_name(NEW.container);
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_archive_locations_normalize() IS
'553 (#2141): stores every archive location row in normalized form, whatever writes it: the store, an older instance, or an operator repointing by hand.';

-- <docs>fundamentals/events/archive-store#archive-locations</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:NormalizeLocations_MergesTheRowsThatAreOneLocation_AndKeepsEveryRecordAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__.wh_normalize_archive_locations() RETURNS BIGINT AS $$
DECLARE
  v_id BIGINT;
  v_row __SCHEMA__.wh_archive_locations%ROWTYPE;
  v_provider TEXT;
  v_endpoint TEXT;
  v_name TEXT;
  v_container TEXT;
  v_into BIGINT;
  v_changed BIGINT := 0;
BEGIN
  FOR v_id IN SELECT location_id FROM __SCHEMA__.wh_archive_locations ORDER BY location_id LOOP
    SELECT * INTO v_row FROM __SCHEMA__.wh_archive_locations WHERE location_id = v_id;
    IF NOT FOUND THEN
      CONTINUE;
    END IF;
    v_provider := __SCHEMA__._wh_normalize_archive_name(v_row.provider);
    v_endpoint := __SCHEMA__._wh_normalize_archive_endpoint(v_row.account_endpoint);
    v_name := __SCHEMA__._wh_normalize_archive_name(v_row.connection_name);
    v_container := __SCHEMA__._wh_normalize_archive_name(v_row.container);
    IF v_row.provider = v_provider AND v_row.account_endpoint IS NOT DISTINCT FROM v_endpoint
       AND v_row.connection_name IS NOT DISTINCT FROM v_name AND v_row.container = v_container THEN
      CONTINUE;
    END IF;
    -- Another row already holds this location in normalized form (the unique index's identity): this row joins it.
    SELECT location_id INTO v_into FROM __SCHEMA__.wh_archive_locations
    WHERE location_id <> v_id AND provider = v_provider AND COALESCE(account_endpoint, '') = COALESCE(v_endpoint, '')
      AND COALESCE(connection_name, '') = COALESCE(v_name, '') AND container = v_container;
    IF v_into IS NOT NULL THEN
      PERFORM __SCHEMA__._wh_merge_archive_location(v_id, v_into);
    ELSE
      UPDATE __SCHEMA__.wh_archive_locations
      SET provider = v_provider, account_endpoint = v_endpoint, connection_name = v_name, container = v_container
      WHERE location_id = v_id;
    END IF;
    v_changed := v_changed + 1;
  END LOOP;
  RETURN v_changed;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_normalize_archive_locations() IS
'553 (#2141): rewrites every archive location row in normalized form; a row that is then the same location as another is merged into it (its manifest rows and queued deletions move, no path changes, no object is touched). Returns the rows changed; a re-run changes none.';

SELECT __SCHEMA__.wh_normalize_archive_locations();

DROP TRIGGER IF EXISTS wh_archive_locations_normalize_trg ON __SCHEMA__.wh_archive_locations;
CREATE TRIGGER wh_archive_locations_normalize_trg
  BEFORE INSERT OR UPDATE ON __SCHEMA__.wh_archive_locations
  FOR EACH ROW EXECUTE FUNCTION __SCHEMA__._wh_archive_locations_normalize();

-- <docs>fundamentals/events/archive-store#archive-locations</docs>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:Backfill_SplitsEveryLegacyLocator_IntoALocationPerContainerAndARelativePath_AndRunsOnceAsync</tests>
-- <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ArchiveLocationSqlTests.cs:OlderInstanceWrite_WithOnlyALocator_IsGivenALocationAndPathAsync</tests>
CREATE OR REPLACE FUNCTION __SCHEMA__._wh_archive_legacy_location_id(p_provider TEXT, p_container TEXT) RETURNS BIGINT AS $$
DECLARE
  v_id BIGINT;
  v_provider TEXT := __SCHEMA__._wh_normalize_archive_name(p_provider);
  v_container TEXT := __SCHEMA__._wh_normalize_archive_name(p_container);
BEGIN
  INSERT INTO __SCHEMA__.wh_archive_locations (provider, container) VALUES (v_provider, v_container)
  ON CONFLICT DO NOTHING RETURNING location_id INTO v_id;
  IF v_id IS NULL THEN
    SELECT location_id INTO v_id FROM __SCHEMA__.wh_archive_locations
    WHERE provider = v_provider AND account_endpoint IS NULL AND connection_name IS NULL AND container = v_container;
  END IF;
  RETURN v_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__._wh_archive_legacy_location_id(TEXT, TEXT) IS
'353 (#2137): the location of a container''s records written before endpoints were recorded (no endpoint, no connection name), created on first use. 553 (#2141): looked up and stored normalized.';

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
  v_provider TEXT := __SCHEMA__._wh_normalize_archive_name(p_provider);
  v_endpoint TEXT := __SCHEMA__._wh_normalize_archive_endpoint(p_account_endpoint);
  v_name TEXT := __SCHEMA__._wh_normalize_archive_name(p_connection_name);
  v_container TEXT := __SCHEMA__._wh_normalize_archive_name(p_container);
BEGIN
  SELECT location_id INTO v_id FROM __SCHEMA__.wh_archive_locations
  WHERE provider = v_provider AND account_endpoint = v_endpoint AND connection_name = v_name
    AND container = v_container;

  -- A container's records written before endpoints were recorded are taken to be at the configured endpoint: the
  -- database never knew another. Records of any other container keep failing the check.
  SELECT location_id INTO v_legacy FROM __SCHEMA__.wh_archive_locations
  WHERE provider = v_provider AND account_endpoint IS NULL AND connection_name IS NULL AND container = v_container
  FOR UPDATE;
  IF v_legacy IS NOT NULL THEN
    IF v_id IS NULL THEN
      UPDATE __SCHEMA__.wh_archive_locations
      SET account_endpoint = v_endpoint, connection_name = v_name
      WHERE location_id = v_legacy;
      RETURN v_legacy;
    END IF;
    PERFORM __SCHEMA__._wh_merge_archive_location(v_legacy, v_id);
  END IF;

  IF v_id IS NULL AND p_create THEN
    INSERT INTO __SCHEMA__.wh_archive_locations (provider, account_endpoint, connection_name, container)
    VALUES (v_provider, v_endpoint, v_name, v_container)
    ON CONFLICT DO NOTHING
    RETURNING location_id INTO v_id;
    IF v_id IS NULL THEN
      SELECT location_id INTO v_id FROM __SCHEMA__.wh_archive_locations
      WHERE provider = v_provider AND account_endpoint = v_endpoint AND connection_name = v_name
        AND container = v_container;
    END IF;
  END IF;
  RETURN v_id;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION __SCHEMA__.wh_archive_location_id(TEXT, TEXT, TEXT, TEXT, BOOLEAN) IS
'353 (#2137): the id of the configured archive location (provider, account endpoint, logical connection name, container), or NULL when none exists and p_create is false (a read, restore or delete never creates one). The container''s legacy location (no endpoint, no name) is taken as the configured one, or merged into it when both exist. 553 (#2141): the location is looked up and stored normalized, so one written another way is the same location.';

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
  v_provider TEXT := __SCHEMA__._wh_normalize_archive_name(p_provider);
  v_endpoint TEXT := __SCHEMA__._wh_normalize_archive_endpoint(p_account_endpoint);
  v_name TEXT := __SCHEMA__._wh_normalize_archive_name(p_connection_name);
  v_container TEXT := __SCHEMA__._wh_normalize_archive_name(p_container);
BEGIN
  SELECT location_id INTO v_to FROM __SCHEMA__.wh_archive_locations
  WHERE provider = v_provider AND account_endpoint = v_endpoint AND connection_name = v_name
    AND container = v_container;
  IF v_to IS NULL THEN
    -- The usual case: the one row changes, and every record of the location follows it.
    UPDATE __SCHEMA__.wh_archive_locations
    SET provider = v_provider, account_endpoint = v_endpoint, connection_name = v_name,
        container = v_container
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

COMMENT ON FUNCTION __SCHEMA__.wh_repoint_archive_location(BIGINT, TEXT, TEXT, TEXT, TEXT) IS
'353 (#2137): the operator''s acknowledgement that a location''s objects are now at another account endpoint or container (a restored or copied database, disaster recovery). Updates the one location row, or, when the target location already exists, moves the records into it and removes the old row. No record''s path is rewritten and no object is moved. Returns the location id the records now name, or NULL when p_from_location_id names no location and the target does not exist. 553 (#2141): the target is looked up and stored normalized.';
