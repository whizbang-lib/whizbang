using System;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the C# reclassification capability on <see cref="EFCoreWorkCoordinator{TContext}"/>
/// (E1 #13c2): <c>ReclassifyEventsEphemeralAsync</c> (invoke the 074 primitive over a type's name set) and
/// <c>CountSourcedEventsForTypesAsync</c> (the read-only drift probe the startup reconciler uses). Verified
/// against a real Postgres so the coordinator SQL runs end-to-end.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
public class EphemeralReclassifyCoordinatorTests : EFCoreTestBase {
  private static string _commitRequest(Guid eventId, Guid streamId, string eventType, int flags) => $$"""
    {
      "instance_id": "{{Guid.NewGuid()}}",
      "service_name": "test", "host_name": "test-host", "process_id": 1,
      "new_outbox_messages": [{
        "MessageId": "{{eventId}}", "Destination": "out-topic",
        "MessageType": "{{eventType}}", "EnvelopeType": null,
        "Envelope": {"Payload": {"OrderId": 42}, "MessageId": "{{eventId}}", "Hops": []},
        "Metadata": {}, "Scope": null, "StreamId": "{{streamId}}", "IsEvent": true, "Flags": {{flags}}
      }]
    }
    """;

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext ctx) {
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _commitAsync(NpgsqlConnection connection, Guid eventId, Guid streamId, string eventType, int flags) {
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", _commitRequest(eventId, streamId, eventType, flags));
    _ = await call.ExecuteScalarAsync();
  }

  [Test]
  public async Task CountSourcedEventsForTypes_CountsOnlyNotYetEphemeralRowsForTheTypesAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    const string typeA = "Whizbang.Tests.CountSourcedA";
    const string typeB = "Whizbang.Tests.CountSourcedB";

    await _commitAsync(connection, Guid.NewGuid(), Guid.NewGuid(), typeA, flags: 0);   // sourced A
    await _commitAsync(connection, Guid.NewGuid(), Guid.NewGuid(), typeA, flags: 0);   // sourced A
    await _commitAsync(connection, Guid.NewGuid(), Guid.NewGuid(), typeA, flags: 8);   // already ephemeral A
    await _commitAsync(connection, Guid.NewGuid(), Guid.NewGuid(), typeB, flags: 0);   // sourced B (other type)

    var coordinator = _coordinator(dbContext);
    var countA = await coordinator.CountSourcedEventsForTypesAsync(new[] { typeA });
    await Assert.That(countA).IsEqualTo(2L)
      .Because("Only the two not-yet-ephemeral events of type A count — the already-ephemeral one and type B are excluded.");

    var countEmpty = await coordinator.CountSourcedEventsForTypesAsync(Array.Empty<string>());
    await Assert.That(countEmpty).IsEqualTo(0L).Because("An empty type set has no drift.");
  }

  [Test]
  public async Task ReclassifyEventsEphemeral_ReclassifiesHistory_AndClearsDriftAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    const string eventType = "Whizbang.Tests.CoordinatorReclassifyEvent";
    var eventId = Guid.NewGuid();
    await _commitAsync(connection, eventId, Guid.NewGuid(), eventType, flags: 0);

    var coordinator = _coordinator(dbContext);
    await Assert.That(await coordinator.CountSourcedEventsForTypesAsync(new[] { eventType })).IsEqualTo(1L)
      .Because("The historical Sourced event is drift for the now-ephemeral type.");

    var result = await coordinator.ReclassifyEventsEphemeralAsync(new[] { eventType });
    await Assert.That(result.EventsReclassified).IsEqualTo(1L).Because("The one historical event is reclassified.");
    await Assert.That(result.StreamsReclassified).IsEqualTo(1L).Because("Its single stream is reclassified.");
    await Assert.That(result.StreamsBlocked).IsEqualTo(0L).Because("A homogeneous stream is never blocked.");

    // Drift is cleared, and the row is now ephemeral with its body offloaded.
    await Assert.That(await coordinator.CountSourcedEventsForTypesAsync(new[] { eventType })).IsEqualTo(0L)
      .Because("After reclassification there is no Sourced drift left.");
    await using (var v = connection.CreateCommand()) {
      v.CommandText = @"SELECT es.flags, (es.event_data IS NULL),
                          (SELECT count(*) FROM wh_event_body eb WHERE eb.event_id = es.event_id)
                        FROM wh_event_store es WHERE es.event_id = @id";
      v.Parameters.AddWithValue("id", eventId);
      await using var r = await v.ExecuteReaderAsync();
      await r.ReadAsync();
      await Assert.That(r.GetInt32(0) & 8).IsEqualTo(8).Because("Now stamped ephemeral.");
      await Assert.That(r.GetBoolean(1)).IsTrue().Because("Inline body moved out.");
      await Assert.That(r.GetInt64(2)).IsEqualTo(1L).Because("Body offloaded to wh_event_body.");
    }
  }

  [Test]
  public async Task ReclassifyEventsEphemeral_RenamedType_MatchesBothNamesViaNameSetAsync() {
    await using var dbContext = CreateDbContext();
    var connection = await _openAsync(dbContext);
    const string currentName = "Whizbang.Tests.CoordinatorRenamedV2";
    const string formerName = "Whizbang.Tests.CoordinatorRenamedV1";
    var stream = Guid.NewGuid();
    await _commitAsync(connection, Guid.NewGuid(), stream, formerName, flags: 0);
    await _commitAsync(connection, Guid.NewGuid(), stream, currentName, flags: 0);

    var coordinator = _coordinator(dbContext);
    var result = await coordinator.ReclassifyEventsEphemeralAsync(new[] { currentName, formerName });
    await Assert.That(result.EventsReclassified).IsEqualTo(2L)
      .Because("Passing the full name set reclassifies the renamed type's history under both names.");
    await Assert.That(result.StreamsBlocked).IsEqualTo(0L)
      .Because("A type's own former-name events are not mistaken for another type.");
  }
}
