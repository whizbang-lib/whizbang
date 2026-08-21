using System;
using System.Data;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <c>GetEphemeralBodiesAboutToReapAsync</c> (E2-2) — the query the maintenance worker
/// uses to fire a destruction hook for each ephemeral body Task 8 is about to reap THIS cycle. It must
/// return exactly the reaper's set: ephemeral (flags&amp;8), consumed (no unprocessed work item), aged past
/// grace, snapshot-covered. Verified against a real Postgres.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
[Category("Shard1")]
public class EphemeralDestructionTargetSqlTests : EFCoreTestBase {
  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _commitEphemeralAsync(NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType) {
    var request = $$"""
      {
        "instance_id": "{{Guid.NewGuid()}}", "service_name": "test", "host_name": "h", "process_id": 1,
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}", "Destination": "out", "MessageType": "{{eventType}}", "EnvelopeType": null,
          "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
          "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": 8
        }]
      }
      """;
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    cmd.Parameters.AddWithValue("req", request);
    await cmd.ExecuteScalarAsync();
  }

  private static async Task _ageAsync(NpgsqlConnection connection, Guid eventId, int minutes) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = $"UPDATE wh_event_store SET created_at = NOW() - INTERVAL '{minutes} minutes' WHERE event_id = @id";
    cmd.Parameters.AddWithValue("id", eventId);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task GetEphemeralBodiesAboutToReap_ConsumedAgedCovered_IsReturned_RecentIsNotAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    // No consuming perspective => no work item => "consumed", vacuously snapshot-covered. Only grace gates it.
    var aged = Guid.NewGuid();
    var agedStream = Guid.NewGuid();
    await _commitEphemeralAsync(connection, aged, agedStream, "Whizbang.Tests.ReapTargetAged");
    await _ageAsync(connection, aged, 10);   // past the 300s default grace

    // A recent ephemeral body — consumed + covered, but NOT yet aged => not about to reap.
    var recent = Guid.NewGuid();
    await _commitEphemeralAsync(connection, recent, Guid.NewGuid(), "Whizbang.Tests.ReapTargetRecent");

    var targets = await coordinator.GetEphemeralBodiesAboutToReapAsync();

    var hit = targets.FirstOrDefault(t => t.EventId == aged);
    await Assert.That(hit).IsNotNull().Because("A consumed, aged-past-grace, covered ephemeral body is about to be reaped.");
    await Assert.That(hit!.StreamId).IsEqualTo(agedStream);
    await Assert.That(hit.EventType).IsEqualTo("Whizbang.Tests.ReapTargetAged");
    await Assert.That(targets.Any(t => t.EventId == recent)).IsFalse()
      .Because("A recent (within-grace) body is not yet reapable, so it is not a destruction target this cycle.");
  }

  [Test]
  public async Task GetEphemeralBodiesAboutToReap_UnprocessedWorkItem_IsExcludedAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    var coordinator = _coordinator(dbContext);

    const string eventType = "Whizbang.Tests.ReapTargetPending";
    // A consuming perspective association => the emit chain creates a wh_perspective_events work item.
    await using (var assoc = connection.CreateCommand()) {
      assoc.CommandText =
        "INSERT INTO wh_message_associations (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at) " +
        "VALUES (gen_random_uuid(), @t, 'perspective', 'PendingP', 'test', @t, NOW(), NOW()) ON CONFLICT DO NOTHING";
      assoc.Parameters.AddWithValue("t", eventType);
      await assoc.ExecuteNonQueryAsync();
    }

    var eventId = Guid.NewGuid();
    await _commitEphemeralAsync(connection, eventId, Guid.NewGuid(), eventType);
    await _ageAsync(connection, eventId, 10);   // aged, but its work item is still UNPROCESSED (not consumed)

    var targets = await coordinator.GetEphemeralBodiesAboutToReapAsync();
    await Assert.That(targets.Any(t => t.EventId == eventId)).IsFalse()
      .Because("An unconsumed body (unprocessed perspective work item) is not about to be reaped, even when aged.");
  }
}
