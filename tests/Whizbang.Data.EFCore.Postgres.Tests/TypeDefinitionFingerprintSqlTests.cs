using System;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for migration 075's type-definition fingerprint storage substrate: the
/// <c>wh_type_definitions</c> table (one row per distinct type-definition-version, keyed by its content
/// hashes) and <c>wh_definition_lineage</c> (edges describing how one definition superseded another).
/// <c>register_type_definition</c> is idempotent by hash and reports whether a definition is new plus the
/// type's previous definition (so the reconciler can record a lineage edge). Verified against a real Postgres.
/// </summary>
/// <docs>fundamentals/events/type-definition-fingerprint</docs>
[Category("Shard1")]
public class TypeDefinitionFingerprintSqlTests : EFCoreTestBase {
  private static byte[] _hash(string seed) {
    // Deterministic 32-byte stand-in for a generator-produced content hash.
    var bytes = new byte[32];
    for (var i = 0; i < seed.Length && i < 32; i++) {
      bytes[i] = (byte)seed[i];
    }
    return bytes;
  }

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task<(int definitionId, bool isNew, int? previousId)> _registerAsync(
      NpgsqlConnection connection, string eventType, byte[] settingsHash, byte[] schemaHash, int schemaVersion) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT definition_id, is_new, previous_definition_id FROM register_type_definition(@t, @sh, @sch, @v)";
    cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = eventType });
    cmd.Parameters.Add(new NpgsqlParameter("sh", NpgsqlDbType.Bytea) { Value = settingsHash });
    cmd.Parameters.Add(new NpgsqlParameter("sch", NpgsqlDbType.Bytea) { Value = schemaHash });
    cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Integer) { Value = schemaVersion });
    await using var r = await cmd.ExecuteReaderAsync();
    await r.ReadAsync();
    var prev = await r.IsDBNullAsync(2) ? (int?)null : r.GetInt32(2);
    return (r.GetInt32(0), r.GetBoolean(1), prev);
  }

  [Test]
  public async Task RegisterTypeDefinition_New_ReturnsIdIsNewNoPreviousAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    var reg = await _registerAsync(connection, "Whizbang.Tests.FirstDefEvent", _hash("settings1"), _hash("schema1"), 1);
    await Assert.That(reg.definitionId).IsGreaterThan(0).Because("A fresh definition gets an id.");
    await Assert.That(reg.isNew).IsTrue().Because("It was never seen before.");
    await Assert.That(reg.previousId).IsNull().Because("The type has no prior definition to link from.");
  }

  [Test]
  public async Task RegisterTypeDefinition_SameHashes_IdempotentSameIdNotNewAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    const string type = "Whizbang.Tests.IdempotentDefEvent";

    var first = await _registerAsync(connection, type, _hash("s"), _hash("sc"), 1);
    var second = await _registerAsync(connection, type, _hash("s"), _hash("sc"), 1);
    await Assert.That(second.definitionId).IsEqualTo(first.definitionId).Because("Same content hashes = same definition row.");
    await Assert.That(second.isNew).IsFalse().Because("Re-registering an identical definition is a no-op, not new.");
  }

  [Test]
  public async Task RegisterTypeDefinition_ChangedSchemaHash_NewDefinitionLinksToPreviousAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    const string type = "Whizbang.Tests.EvolvingDefEvent";

    var v1 = await _registerAsync(connection, type, _hash("settings"), _hash("schemaV1"), 1);
    var v2 = await _registerAsync(connection, type, _hash("settings"), _hash("schemaV2"), 2);

    await Assert.That(v2.isNew).IsTrue().Because("A changed schema hash is a new definition.");
    await Assert.That(v2.definitionId).IsNotEqualTo(v1.definitionId).Because("It gets a distinct id.");
    await Assert.That(v2.previousId).IsEqualTo(v1.definitionId)
      .Because("register reports the type's prior definition so the reconciler can record a lineage edge.");
  }

  [Test]
  public async Task RecordDefinitionLineage_CreatesEdgeQueryableAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    const string type = "Whizbang.Tests.LineageDefEvent";

    var v1 = await _registerAsync(connection, type, _hash("s1"), _hash("sc1"), 1);
    var v2 = await _registerAsync(connection, type, _hash("s1"), _hash("sc2"), 2);

    await using (var edge = connection.CreateCommand()) {
      // relationship 0 = SchemaUpgradedTo
      edge.CommandText = "SELECT record_definition_lineage(@from, @to, 0::smallint, @ref)";
      edge.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Integer) { Value = v1.definitionId });
      edge.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Integer) { Value = v2.definitionId });
      edge.Parameters.Add(new NpgsqlParameter("ref", NpgsqlDbType.Text) { Value = "UpcastLineageDefEventV1ToV2" });
      await edge.ExecuteNonQueryAsync();
    }

    await using (var q = connection.CreateCommand()) {
      q.CommandText = "SELECT relationship, migration_ref FROM wh_definition_lineage WHERE from_definition_id = @from AND to_definition_id = @to";
      q.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Integer) { Value = v1.definitionId });
      q.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Integer) { Value = v2.definitionId });
      await using var r = await q.ExecuteReaderAsync();
      await Assert.That(await r.ReadAsync()).IsTrue().Because("The lineage edge is stored.");
      await Assert.That(r.GetInt16(0)).IsEqualTo((short)0).Because("Relationship is SchemaUpgradedTo.");
      await Assert.That(r.GetString(1)).IsEqualTo("UpcastLineageDefEventV1ToV2").Because("The bound migration ref is stored.");
    }
  }
}
