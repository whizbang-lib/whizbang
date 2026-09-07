using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Migration 142 (issue #697): <c>reconcile_perspective_registry</c> adopts a row that was keyed in
/// the previous display-string form (<c>Outer.Model</c>) when the generator now presents the CLR
/// key (<c>Outer+Model</c>) for the same table and service. Inserting a second row would leave the
/// old one stale with the row-retention enrollment on neither.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
[Category("Shard3")]
public class ReconcilePerspectiveRegistryKeyAdoptionSqlTests : EFCoreTestBase {
  private const string TABLE = "wh_per_key_adoption";
  private const string SERVICE = "svc-key-adoption";
  private const string DOTTED_KEY = "TestApp.Owner.Model";
  private const string CLR_KEY = "TestApp.Owner+Model";

  [Test]
  public async Task Reconcile_ExistingDisplayStringKey_IsAdoptedNotDuplicatedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _seedDottedRowAsync(conn, enrolled: true, ttlSeconds: 86400);

    var report = await _reconcileAsync(conn, CLR_KEY, TABLE);

    await Assert.That(report.Count).IsEqualTo(1);
    await Assert.That(report[0].Action).IsEqualTo("renamed_key");
    await Assert.That(report[0].ClrTypeName).IsEqualTo(CLR_KEY);

    var rows = await _rowsForTableAsync(conn);
    await Assert.That(rows.Count).IsEqualTo(1)
      .Because("the dotted row is adopted, not duplicated");
    await Assert.That(rows[0].ClrTypeName).IsEqualTo(CLR_KEY);
    await Assert.That(rows[0].Enrolled).IsTrue()
      .Because("the adopted row keeps its enrollment columns");
    await Assert.That(rows[0].TtlSeconds).IsEqualTo(86400);
  }

  [Test]
  public async Task Reconcile_SameKeyAgain_IsAnOrdinaryUpdateAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _seedDottedRowAsync(conn, enrolled: false, ttlSeconds: null);
    _ = await _reconcileAsync(conn, CLR_KEY, TABLE);

    var second = await _reconcileAsync(conn, CLR_KEY, TABLE);

    await Assert.That(second[0].Action).IsEqualTo("updated")
      .Because("once adopted, the CLR key is found directly");
    await Assert.That((await _rowsForTableAsync(conn)).Count).IsEqualTo(1);
  }

  [Test]
  public async Task Reconcile_UnknownKeyForAnUnknownTable_IsInsertedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _seedDottedRowAsync(conn, enrolled: true, ttlSeconds: 60);

    var report = await _reconcileAsync(conn, "TestApp.Other+Model", "wh_per_key_adoption_other");

    await Assert.That(report[0].Action).IsEqualTo("inserted")
      .Because("adoption keys on the table; a different table is a different perspective");
    await Assert.That((await _rowsForTableAsync(conn)).Count).IsEqualTo(1)
      .Because("the dotted row for the original table is untouched");
  }

  [Test]
  public async Task Reconcile_DottedRowOfAnotherService_IsNotAdoptedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await _seedDottedRowAsync(conn, enrolled: true, ttlSeconds: 60, service: "some-other-service");

    var report = await _reconcileAsync(conn, CLR_KEY, TABLE);

    await Assert.That(report[0].Action).IsEqualTo("inserted")
      .Because("the registry is per service; another service's row is never rewritten");
  }

  // -------------------------------------------------------------------------------------------

  private sealed record ReportRow(string Action, string ClrTypeName);
  private sealed record RegistryRow(string ClrTypeName, bool Enrolled, int? TtlSeconds);

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task _seedDottedRowAsync(NpgsqlConnection conn, bool enrolled, int? ttlSeconds, string service = SERVICE) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_perspective_registry
        (clr_type_name, table_name, schema_json, schema_hash, service_name, row_retention_enrolled, row_ttl_seconds)
      VALUES (@clr, @table, '{}'::jsonb, 'h', @svc, @enrolled, @ttl)";
    cmd.Parameters.AddWithValue("clr", DOTTED_KEY);
    cmd.Parameters.AddWithValue("table", TABLE);
    cmd.Parameters.AddWithValue("svc", service);
    cmd.Parameters.AddWithValue("enrolled", enrolled);
    cmd.Parameters.Add(new NpgsqlParameter("ttl", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)ttlSeconds ?? DBNull.Value });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<List<ReportRow>> _reconcileAsync(NpgsqlConnection conn, string clrKey, string table) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT action, clr_type_name FROM reconcile_perspective_registry(@p::jsonb, @svc)";
    cmd.Parameters.AddWithValue("p", $$"""
      [{"ClrTypeName":"{{clrKey}}","TableName":"{{table}}","SchemaJson":{},"SchemaHash":"h","ServiceName":"{{SERVICE}}"}]
      """);
    cmd.Parameters.AddWithValue("svc", SERVICE);
    var rows = new List<ReportRow>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new ReportRow(reader.GetString(0), reader.GetString(1)));
    }
    return rows;
  }

  private static async Task<List<RegistryRow>> _rowsForTableAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT clr_type_name, row_retention_enrolled, row_ttl_seconds FROM wh_perspective_registry WHERE table_name = @t ORDER BY clr_type_name";
    cmd.Parameters.AddWithValue("t", TABLE);
    var rows = new List<RegistryRow>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      rows.Add(new RegistryRow(reader.GetString(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
    }
    return rows;
  }
}
