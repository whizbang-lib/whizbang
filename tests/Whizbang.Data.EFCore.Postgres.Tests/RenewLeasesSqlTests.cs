using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for <c>renew_leases</c> — per-category batched lease extension. Called by
/// the C# LeaseRenewalWorker when in-flight items approach expiry. Coalesced flush.
/// Phase A of the work-pump decomposition.
/// </summary>
/// <docs>fundamentals/work-coordinator/batched-flushers</docs>
[Category("Shard4")]
public class RenewLeasesSqlTests : EFCoreTestBase {

  [Test]
  public async Task RenewLeases_FunctionExists_InPublicSchemaAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname='renew_leases' AND pronamespace='public'::regnamespace);";
    var exists = (bool)(await command.ExecuteScalarAsync())!;
    await Assert.That(exists).IsTrue();
  }

  [Test]
  public async Task RenewLeases_OutboxCategory_ExtendsLeaseExpiryAsync() {
    await using var dbContext = CreateDbContext();
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }

    var instanceId = Guid.NewGuid();
    var msgId = Guid.NewGuid();

    await using (var ins = connection.CreateCommand()) {
      ins.CommandText = @"
        INSERT INTO wh_outbox
          (message_id, destination, message_type, event_data, metadata, status, attempts, created_at,
           instance_id, lease_expiry, stream_id, partition_number)
        VALUES (@msg, 'test-topic', 'TestEvent', '{}', '{}', 1, 0, NOW(),
                @inst, NOW() + INTERVAL '5 seconds', @stream, 0)";
      ins.Parameters.AddWithValue("msg", msgId);
      ins.Parameters.AddWithValue("inst", instanceId);
      ins.Parameters.AddWithValue("stream", Guid.NewGuid());
      await ins.ExecuteNonQueryAsync();
    }

    DateTime initialExpiry;
    await using (var read = connection.CreateCommand()) {
      read.CommandText = "SELECT lease_expiry FROM wh_outbox WHERE message_id = @msg";
      read.Parameters.AddWithValue("msg", msgId);
      initialExpiry = Convert.ToDateTime(await read.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    await using (var call = connection.CreateCommand()) {
      call.CommandText = "SELECT renew_leases('outbox', @ids, 300)";
      call.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = new[] { msgId } });
      _ = await call.ExecuteScalarAsync();
    }

    DateTime newExpiry;
    await using (var read = connection.CreateCommand()) {
      read.CommandText = "SELECT lease_expiry FROM wh_outbox WHERE message_id = @msg";
      read.Parameters.AddWithValue("msg", msgId);
      newExpiry = Convert.ToDateTime(await read.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    await Assert.That(newExpiry).IsGreaterThan(initialExpiry);
  }
}
