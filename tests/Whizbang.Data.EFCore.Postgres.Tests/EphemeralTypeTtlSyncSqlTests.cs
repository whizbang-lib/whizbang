using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the E2-4b TTL sync primitive (migration 080). <c>sync_ephemeral_type_ttl</c> is a
/// full-replace: it upserts the declared <c>[Ephemeral(TtlSeconds)]</c> set and prunes any override no
/// longer declared, so the <c>wh_ephemeral_type_ttl</c> lookup always mirrors the current catalog. Verified
/// against a real Postgres.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class EphemeralTypeTtlSyncSqlTests : EFCoreTestBase {
  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _syncAsync(NpgsqlConnection connection, string[] names, int[] ttls) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT sync_ephemeral_type_ttl(@names, @ttls)";
    cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = names });
    cmd.Parameters.Add(new NpgsqlParameter("ttls", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer) { Value = ttls });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int?> _ttlAsync(NpgsqlConnection connection, string name) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT ttl_seconds FROM wh_ephemeral_type_ttl WHERE event_type = normalize_event_type(@n)";
    cmd.Parameters.AddWithValue("n", name);
    var result = await cmd.ExecuteScalarAsync();
    return result is null or System.DBNull ? null : (int)result;
  }

  [Test]
  public async Task Sync_UpsertsUpdatesAndPrunes_FullReplaceAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    // First declaration: two age-gated types.
    await _syncAsync(connection, ["Whizbang.Tests.TtlA", "Whizbang.Tests.TtlB"], [100, 200]);
    await Assert.That(await _ttlAsync(connection, "Whizbang.Tests.TtlA")).IsEqualTo(100);
    await Assert.That(await _ttlAsync(connection, "Whizbang.Tests.TtlB")).IsEqualTo(200);

    // Second declaration: A's TTL changed, B is gone. Full-replace must update A and prune B.
    await _syncAsync(connection, ["Whizbang.Tests.TtlA"], [150]);
    await Assert.That(await _ttlAsync(connection, "Whizbang.Tests.TtlA")).IsEqualTo(150)
      .Because("A re-declared type has its TTL upserted to the new value.");
    await Assert.That(await _ttlAsync(connection, "Whizbang.Tests.TtlB")).IsNull()
      .Because("A type no longer declared with a TTL is pruned — the lookup mirrors the current catalog.");
  }

  [Test]
  public async Task Sync_EmptyInput_ClearsAllOverridesAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);

    await _syncAsync(connection, ["Whizbang.Tests.TtlClearMe"], [300]);
    await Assert.That(await _ttlAsync(connection, "Whizbang.Tests.TtlClearMe")).IsEqualTo(300);

    await _syncAsync(connection, [], []);
    await Assert.That(await _ttlAsync(connection, "Whizbang.Tests.TtlClearMe")).IsNull()
      .Because("Empty input clears every override — no type has an age-based TTL.");
  }
}
