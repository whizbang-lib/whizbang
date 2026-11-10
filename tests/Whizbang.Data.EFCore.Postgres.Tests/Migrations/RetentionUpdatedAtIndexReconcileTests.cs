// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Schema;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The <c>updated_at</c> index of a perspective table that declares row retention (#2087): the managed-object
/// reconcile reads enrollment from <c>wh_perspective_registry</c>, the one place the reapers read it, builds the index
/// an enrolled table lacks and keeps it, and retires the one a table without retention or a declaration still has.
/// </summary>
/// <remarks>
/// Each run below is one reconcile, as a start or the periodic maintenance step runs it. A Whizbang-built object no
/// longer declared is retired on the run after the one that first records it, so the retiring cases run twice.
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes#the-updated_at-index</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/PerspectiveRetentionIndexes.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/ManagedSchemaReconciler.cs</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class RetentionUpdatedAtIndexReconcileTests : EFCoreTestBase {
  private const string SCHEMA = "public";
  private const string TABLE = "wh_per_retention_index_probe";
  private const string INDEX = "idx_retention_index_probe_updated_at";
  private const string CLR = "Probe.RetentionIndexProbeModel";

  [Before(Test)]
  public async Task CreateProbeTableAsync() =>
    await _execAsync($"""
      CREATE TABLE {TABLE} (
        id uuid PRIMARY KEY, data jsonb NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
        updated_at timestamptz NOT NULL DEFAULT now());
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
      VALUES ('{CLR}', '{TABLE}', '{"{}"}'::jsonb, 'probe-hash', 'probe-svc');
      """);

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<T> _scalarAsync<T>(string sql) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (T)(await command.ExecuteScalarAsync())!;
  }

  private Task _enrollAsync() =>
    _execAsync($"UPDATE wh_perspective_registry SET row_retention_enrolled = TRUE WHERE clr_type_name = '{CLR}'");

  private Task<bool> _existsAsync(string index) =>
    _scalarAsync<bool>($"SELECT to_regclass('public.{index}') IS NOT NULL");

  private Task<long> _updatedAtIndexCountAsync() =>
    _scalarAsync<long>($"""
      SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{TABLE}'
        AND indexdef LIKE '%USING btree (updated_at)'
      """);

  private async Task<ManagedSchemaReport> _reconcileAsync(
      ReconcileMode mode = ReconcileMode.Apply, ManagedSchemaObjectSet? declared = null) {
    await using var db = new NpgsqlConnection(ConnectionString);
    await db.OpenAsync();
    return await ManagedSchemaReconciler.RunAsync(
      db, SCHEMA, declared ?? new ManagedSchemaObjectSet(), new ManagedSchemaSettings(mode, [], [], DropAfterFleetConverged: false),
      instanceId: null, logger: null, CancellationToken.None);
  }

  // ========================================
  // Without retention
  // ========================================

  /// <summary>An existing table's index is retired when the table neither declares retention nor declares the index.</summary>
  [Test]
  public async Task WithoutRetentionOrADeclaration_TheOldIndexIsRetiredAsync() {
    await _execAsync($"CREATE INDEX {INDEX} ON {TABLE} (updated_at)");

    await _reconcileAsync();
    await Assert.That(await _existsAsync(INDEX)).IsTrue().Because("the run that first records an object drops nothing");

    await _reconcileAsync();
    await Assert.That(await _existsAsync(INDEX)).IsFalse()
      .Because("nothing asks for it, and a btree on updated_at keeps every update from being HOT");
  }

  /// <summary>A table without retention gets no index built for it.</summary>
  [Test]
  public async Task WithoutRetention_NoIndexIsBuiltAsync() {
    await _reconcileAsync();
    await _reconcileAsync();

    await Assert.That(await _updatedAtIndexCountAsync()).IsEqualTo(0L);
  }

  /// <summary>A model that declares the index keeps it, with no retention.</summary>
  [Test]
  public async Task ADeclaredIndex_IsKeptWithoutRetentionAsync() {
    await _execAsync($"CREATE INDEX {INDEX} ON {TABLE} (updated_at)");
    var declared = new ManagedSchemaObjectSet().Index(TABLE, INDEX, "RetentionIndexProbeModel [PerspectiveIndex]");

    await _reconcileAsync(declared: declared);
    await _reconcileAsync(declared: declared);

    await Assert.That(await _existsAsync(INDEX)).IsTrue();
  }

  // ========================================
  // With retention
  // ========================================

  /// <summary>An enrolled table's existing index is kept, and the ledger records why.</summary>
  [Test]
  public async Task WithRetention_TheExistingIndexIsKeptAsync() {
    await _execAsync($"CREATE INDEX {INDEX} ON {TABLE} (updated_at)");
    await _enrollAsync();

    await _reconcileAsync();
    await _reconcileAsync();

    await Assert.That(await _existsAsync(INDEX)).IsTrue()
      .Because("the sliding-TTL and row-cap reapers range-scan updated_at on an enrolled table");
    await Assert.That(await _scalarAsync<string>(
      $"SELECT status FROM wh_managed_objects WHERE table_name = '{TABLE}' AND object_name = '{INDEX}'"))
      .IsEqualTo(ManagedStatuses.ACTIVE);
    await Assert.That(await _scalarAsync<string>(
      $"SELECT declared_by FROM wh_managed_objects WHERE table_name = '{TABLE}' AND object_name = '{INDEX}'"))
      .Contains("row retention");
  }

  /// <summary>An enrolled table without the index gets it, and a later run builds no second one.</summary>
  [Test]
  public async Task WithRetention_AMissingIndexIsBuilt_OnceAsync() {
    await _enrollAsync();

    var first = await _reconcileAsync();
    await Assert.That(await _existsAsync(INDEX)).IsTrue();
    await Assert.That(first.Missing.Select(m => m.Name)).DoesNotContain(INDEX)
      .Because("it was built before the plan read the table, so it is not reported missing");

    await _reconcileAsync();
    await _reconcileAsync();

    await Assert.That(await _updatedAtIndexCountAsync()).IsEqualTo(1L).Because("the reconcile is idempotent");
    await Assert.That(await _existsAsync(INDEX)).IsTrue();
  }

  /// <summary>AddOnly may build what is declared.</summary>
  [Test]
  public async Task InAddOnlyMode_AMissingIndexIsBuiltAsync() {
    await _enrollAsync();

    await _reconcileAsync(ReconcileMode.AddOnly);

    await Assert.That(await _existsAsync(INDEX)).IsTrue();
  }

  /// <summary>ReportOnly changes nothing: the missing index is reported, not built.</summary>
  [Test]
  public async Task InReportOnlyMode_AMissingIndexIsReported_NotBuiltAsync() {
    await _enrollAsync();

    var report = await _reconcileAsync(ReconcileMode.ReportOnly);

    await Assert.That(await _existsAsync(INDEX)).IsFalse();
    await Assert.That(report.Missing.Select(m => m.Name)).Contains(INDEX);
  }

  /// <summary>
  /// An enrolled table whose index stands under another name (the Dapper driver's former name) keeps that one, with no
  /// twin built beside it.
  /// </summary>
  [Test]
  public async Task WithRetention_AnEquivalentUnderAnotherName_IsKept_AndNotDuplicatedAsync() {
    var dapperName = $"ix_{TABLE}_updated_at";
    await _execAsync($"CREATE INDEX {dapperName} ON {TABLE} (updated_at)");
    await _enrollAsync();

    await _reconcileAsync();
    await _reconcileAsync();
    await _reconcileAsync();

    await Assert.That(await _updatedAtIndexCountAsync()).IsEqualTo(1L);
    await Assert.That(await _existsAsync(dapperName)).IsTrue()
      .Because("it has the same definition, so it stands for the declared index rather than being retired");
  }

  /// <summary>An enrolled registry row whose table is gone, or lacks the column, is passed over without failing the run.</summary>
  [Test]
  public async Task AnEnrolledRowWithNoUsableTable_IsPassedOverAsync() {
    await _execAsync("""
      CREATE TABLE wh_per_retention_no_column (id uuid PRIMARY KEY, data jsonb NOT NULL);
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name, row_retention_enrolled)
      VALUES ('Probe.Gone', 'wh_per_retention_gone', '{}'::jsonb, 'h', 'probe-svc', TRUE),
             ('Probe.NoColumn', 'wh_per_retention_no_column', '{}'::jsonb, 'h', 'probe-svc', TRUE);
      """);
    await _enrollAsync();

    var report = await _reconcileAsync();

    await Assert.That(report.Skipped).IsFalse();
    await Assert.That(await _existsAsync(INDEX)).IsTrue().Because("the usable enrolled table is still served");
    await Assert.That(report.Missing.Select(m => m.Table)).DoesNotContain("wh_per_retention_gone");
    await Assert.That(report.Missing.Select(m => m.Table)).DoesNotContain("wh_per_retention_no_column");
  }

  /// <summary>A long table name gets the index name the schema generators derive, kept within the identifier limit.</summary>
  [Test]
  public async Task ALongTableName_GetsTheGeneratorsIndexNameAsync() {
    var table = "wh_per_" + new string('r', 50);
    await _execAsync($"""
      CREATE TABLE {table} (id uuid PRIMARY KEY, updated_at timestamptz NOT NULL DEFAULT now());
      INSERT INTO wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name, row_retention_enrolled)
      VALUES ('Probe.Long', '{table}', '{"{}"}'::jsonb, 'h', 'probe-svc', TRUE);
      """);

    await _reconcileAsync();

    var name = PerspectiveRetentionIndexes.IndexName(table);
    await Assert.That(name.Length).IsLessThanOrEqualTo(63);
    await Assert.That(await _existsAsync(name)).IsTrue();
  }
}
