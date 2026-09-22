using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks E1 #13b4-1: <see cref="EFCoreEventStore{TDbContext}"/> reads must be BODY-AWARE. Since the
/// ephemeral body offload (072), an ephemeral event's payload/metadata live in <c>wh_event_body</c> and the
/// <c>wh_event_store</c> inline columns are NULL — so entity-materializing reads (rewind's
/// <c>ReadPolymorphicAsync</c>, saga catch-up's <c>GetEventsBetween*</c>, typed <c>ReadAsync</c>) previously
/// blew up on the NULL inline columns and could never see an in-grace ephemeral body. The store now reads
/// body-first with inline fallback (the same COALESCE the SQL readers use), and SKIPS reaped rows
/// (pointer-only, both NULL) instead of throwing. Verified against a real Postgres through the real SQL
/// emit chain.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
[Category("Shard3")]
public class EphemeralEventStoreReadTests : EFCoreTestBase {
  private static string _commitRequest(Guid eventId, Guid streamId, string eventType, int flags, string payloadJson) => $$"""
    {
      "instance_id": "{{Guid.NewGuid()}}",
      "service_name": "test",
      "host_name": "test-host",
      "process_id": 1,
      "new_outbox_messages": [{
        "MessageId": "{{eventId}}",
        "Destination": "out-topic",
        "MessageType": "{{eventType}}",
        "EnvelopeType": null,
        "Envelope": {"Payload": {{payloadJson}}, "MessageId": "{{eventId}}", "Hops": []},
        "Metadata": {},
        "Scope": null,
        "StreamId": "{{streamId}}",
        "IsEvent": true,
        "Flags": {{flags}}
      }]
    }
    """;

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return connection;
  }

  private static async Task _commitEphemeralOrderAsync(NpgsqlConnection connection, Guid eventId, Guid streamId, Guid orderId, string customer, int flags = 8) {
    var payload = $$"""{"OrderId": "{{orderId}}", "CustomerName": "{{customer}}"}""";
    await using var call = connection.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", _commitRequest(eventId, streamId, typeof(OrderCreatedEvent).FullName!, flags, payload));
    _ = await call.ExecuteScalarAsync();
  }

  [Test]
  public async Task ReadPolymorphicAsync_EphemeralOffloadedBody_ReturnsPayloadAsync() {
    // The rewind path: the generated runner replays via ReadPolymorphicAsync. An in-grace ephemeral
    // rewind depends on this reader seeing the OFFLOADED body (inline is NULL from birth for ephemeral).
    await using var context = CreateDbContext();
    var connection = await _openAsync(context);
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var eventId = (Guid)TrackedGuid.NewMedo();   // MessageId requires UUIDv7
    var orderId = Guid.NewGuid();
    await _commitEphemeralOrderAsync(connection, eventId, streamId, orderId, "PresenceUser");

    var envelopes = new List<MessageEnvelope<IEvent>>();
    await foreach (var env in eventStore.ReadPolymorphicAsync(streamId, null, [typeof(OrderCreatedEvent)])) {
      envelopes.Add(env);
    }

    await Assert.That(envelopes.Count).IsEqualTo(1)
      .Because("An in-grace ephemeral event's body lives in wh_event_body; the rewind reader must see it.");
    var payload = (OrderCreatedEvent)envelopes[0].Payload;
    await Assert.That(payload.OrderId).IsEqualTo(orderId);
    await Assert.That(payload.CustomerName).IsEqualTo("PresenceUser");
  }

  [Test]
  public async Task ReadAsync_FromEventId_EphemeralOffloadedBody_ReturnsPayloadAsync() {
    await using var context = CreateDbContext();
    var connection = await _openAsync(context);
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var orderId = Guid.NewGuid();
    await _commitEphemeralOrderAsync(connection, (Guid)TrackedGuid.NewMedo(), streamId, orderId, "TypedReader");

    var envelopes = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var env in eventStore.ReadAsync<OrderCreatedEvent>(streamId, (Guid?)null)) {
      envelopes.Add(env);
    }

    await Assert.That(envelopes.Count).IsEqualTo(1);
    await Assert.That(envelopes[0].Payload.OrderId).IsEqualTo(orderId);
  }

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_EphemeralOffloadedBody_ReturnsPayloadAsync() {
    // Lifecycle receptors load just-processed events via GetEventsBetween* — for an ephemeral stream those
    // bodies are offloaded, so this path must be body-aware too.
    await using var context = CreateDbContext();
    var connection = await _openAsync(context);
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var eventId = (Guid)TrackedGuid.NewMedo();   // MessageId requires UUIDv7
    var orderId = Guid.NewGuid();
    await _commitEphemeralOrderAsync(connection, eventId, streamId, orderId, "LifecycleReader");

    var envelopes = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId, afterEventId: null, upToEventId: Guid.Empty, [typeof(OrderCreatedEvent)]);

    await Assert.That(envelopes.Count).IsEqualTo(1);
    await Assert.That(((OrderCreatedEvent)envelopes[0].Payload).OrderId).IsEqualTo(orderId);
  }

  [Test]
  public async Task ReadPolymorphicAsync_ReapedEphemeralBody_IsSkippedNotThrownAsync() {
    // A reaped ephemeral event is pointer-only (inline NULL, no wh_event_body row). Readers must SKIP it —
    // never throw mid-stream. (The rewind guard means out-of-grace stragglers don't replay; a reaped row
    // encountered mid-read is consumed + snapshot-covered history, safe to pass over.)
    await using var context = CreateDbContext();
    var connection = await _openAsync(context);
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var reapedId = (Guid)TrackedGuid.NewMedo();
    var aliveId = (Guid)TrackedGuid.NewMedo();
    var aliveOrderId = Guid.NewGuid();
    await _commitEphemeralOrderAsync(connection, reapedId, streamId, Guid.NewGuid(), "Reaped");
    await _commitEphemeralOrderAsync(connection, aliveId, streamId, aliveOrderId, "Alive");

    // Simulate the tier-1 reap of the first body: pointer stays, body row deleted.
    await using (var reap = connection.CreateCommand()) {
      reap.CommandText = "DELETE FROM wh_event_body WHERE event_id = @id";
      reap.Parameters.AddWithValue("id", reapedId);
      await reap.ExecuteNonQueryAsync();
    }

    var envelopes = new List<MessageEnvelope<IEvent>>();
    await foreach (var env in eventStore.ReadPolymorphicAsync(streamId, null, [typeof(OrderCreatedEvent)])) {
      envelopes.Add(env);
    }

    await Assert.That(envelopes.Count).IsEqualTo(1)
      .Because("The reaped pointer-only row is skipped; the alive body is still returned.");
    await Assert.That(((OrderCreatedEvent)envelopes[0].Payload).OrderId).IsEqualTo(aliveOrderId);
  }
}
