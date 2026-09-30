using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The applied-event ledger (migration 177, #959) through the real inbound path: an event published by
/// another service is stored in this service's inbox, chained into the local event store and perspective
/// work when its inbox row is claimed, and recorded as applied when the perspective's completion retires
/// its work row. A wait in this service completes only then.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/177_PerspectiveAppliedLedger.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
/// <docs>fundamentals/perspectives/perspective-sync#applied-ledger</docs>
[Category("Integration")]
[Category("Shard2")]
public class PerspectiveAppliedLedgerSqlTests : EFCoreTestBase {
  private const string PERSPECTIVE = "Ledger.Tests.OrderViewPerspective";
  private const string EVENT_TYPE = "Ledger.Tests.OrderShipped";

  private static EFCoreWorkCoordinator<WorkCoordinationDbContext> _coordinator(WorkCoordinationDbContext ctx) =>
    new(ctx, JsonContextRegistry.CreateCombinedOptions());

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  /// <summary>An event as service A published it, arriving in this service's inbox.</summary>
  private static InboxMessage _fromAnotherService(Guid eventId, Guid streamId, EventFlags flags = EventFlags.None) {
    var envelope = new MessageEnvelope<JsonElement>(MessageId.From(eventId), JsonDocument.Parse("{\"p\":1}").RootElement, []);
    return new InboxMessage {
      MessageId = eventId,
      HandlerName = "LedgerHandler",
      Envelope = envelope,
      EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{EVENT_TYPE}]], Whizbang.Core",
      MessageType = EVENT_TYPE,
      StreamId = streamId,
      IsEvent = true,
      Flags = flags,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(eventId), Hops = [] },
    };
  }

  private static async Task _associateAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @t, 'perspective', @p, 'service-b', @t, NOW(), NOW())
      ON CONFLICT DO NOTHING";
    cmd.Parameters.AddWithValue("t", EVENT_TYPE);
    cmd.Parameters.AddWithValue("p", PERSPECTIVE);
    await cmd.ExecuteNonQueryAsync();
  }

  /// <summary>Claims the inbox row, which chains the event into the event store and creates its perspective work.</summary>
  private static async Task _claimAsync(NpgsqlConnection conn) {
    var instance = Guid.CreateVersion7();
    await using (var register = conn.CreateCommand()) {
      register.CommandText = @"
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
        VALUES (@inst, 'service-b', 'test-host', 1, NOW(), NOW())";
      register.Parameters.AddWithValue("inst", instance);
      await register.ExecuteNonQueryAsync();
    }
    await using var claim = conn.CreateCommand();
    claim.CommandText = "SELECT count(*) FROM claim_work(@inst, 'service-b', 'test-host', 1, 10, 100, 300, 0.5, 10, FALSE, NULL)";
    claim.Parameters.AddWithValue("inst", instance);
    _ = await claim.ExecuteScalarAsync();
  }

  private static async Task<Guid> _workIdAsync(NpgsqlConnection conn, Guid eventId, string perspectiveName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_work_id FROM wh_perspective_events WHERE event_id = @e AND perspective_name = @p";
    cmd.Parameters.AddWithValue("e", eventId);
    cmd.Parameters.AddWithValue("p", perspectiveName);
    var id = await cmd.ExecuteScalarAsync();
    await Assert.That(id).IsNotNull().Because($"claiming the inbox row creates the {perspectiveName} work row");
    return (Guid)id!;
  }

  private static async Task<long> _ledgerCountAsync(NpgsqlConnection conn, Guid eventId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_perspective_applied WHERE event_id = @e";
    cmd.Parameters.AddWithValue("e", eventId);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  private static PerspectiveSyncAwaiter _awaiter(IWorkCoordinator coordinator, TimeProvider time) =>
    new(coordinator,
      new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled }),
      NullLogger<PerspectiveSyncAwaiter>.Instance,
      new SyncEventTracker(),
      new ScopedEventTracker(),
      new NoLifecycleContext(),
      time);

  private sealed class OrderViewPerspective;

  /// <summary>No lifecycle stage is active: the wait is not made from inside an Inline receptor.</summary>
  private sealed class NoLifecycleContext : ILifecycleContextAccessor {
    public ILifecycleContext? Current { get; set; }
  }

  [Test]
  public async Task AnEventFromAnotherService_AwaitedHere_CompletesOnlyAfterThisServicesPerspectiveAppliesItAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var conn = await _openAsync(ctx);
    await _associateAsync(conn);
    var eventId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var perspective = TypeNameFormatter.GetPerspectiveName(typeof(OrderViewPerspective));
    await using (var associate = conn.CreateCommand()) {
      associate.CommandText = @"
        INSERT INTO wh_message_associations
          (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
        VALUES (gen_random_uuid(), @t, 'perspective', @p, 'service-b', @t, NOW(), NOW())";
      associate.Parameters.AddWithValue("t", EVENT_TYPE);
      associate.Parameters.AddWithValue("p", perspective);
      await associate.ExecuteNonQueryAsync();
    }
    var inquiry = new AppliedEventInquiry(perspective, eventId);

    await Assert.That((await coordinator.GetAppliedEventStatusAsync(inquiry))!.Value.State)
      .IsEqualTo(AppliedEventState.NotArrived).Because("service A has not published it yet");

    // Service A publishes; it reaches service B's inbox.
    await coordinator.StoreInboxMessagesAsync([_fromAnotherService(eventId, streamId)], partitionCount: 100);
    await Assert.That((await coordinator.GetAppliedEventStatusAsync(inquiry))!.Value.State)
      .IsEqualTo(AppliedEventState.NotArrived)
      .Because("an inbox row is not yet in the event store; the claim stores it");

    // The wait starts in service B before the event is even stored here.
    var time = new FakeTimeProvider();
    await using var awaiterCtx = CreateDbContext();
    var waiting = _awaiter(_coordinator(awaiterCtx), time)
      .WaitForAppliedAsync(typeof(OrderViewPerspective), eventId, TimeSpan.FromMinutes(1));

    await _claimAsync(conn);
    await Assert.That((await coordinator.GetAppliedEventStatusAsync(inquiry))!.Value.State)
      .IsEqualTo(AppliedEventState.Pending);
    await Assert.That(waiting.IsCompleted).IsFalse()
      .Because("the event is in this service but its perspective has not applied it");

    // This service's perspective worker commits the apply and its completion flush retires the work row.
    await coordinator.CompletePerspectiveAsync(cursors: [], eventWorkIds: [await _workIdAsync(conn, eventId, perspective)], debugMode: false);
    time.Advance(TimeSpan.FromMilliseconds(50));

    var result = await waiting;
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
    await Assert.That((await coordinator.GetAppliedEventStatusAsync(inquiry))!.Value.State)
      .IsEqualTo(AppliedEventState.Applied);
  }

  [Test]
  public async Task ACollectiveEvent_IsAppliedForAnyPerspective_WhenTheSinkCompletesItAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var conn = await _openAsync(ctx);
    var eventId = Guid.CreateVersion7();

    await coordinator.StoreInboxMessagesAsync(
      [_fromAnotherService(eventId, Guid.CreateVersion7(), EventFlags.Collective)], partitionCount: 100);
    await _claimAsync(conn);
    var inquiry = new AppliedEventInquiry(PERSPECTIVE, eventId);

    await Assert.That((await coordinator.GetAppliedEventStatusAsync(inquiry))!.Value.State)
      .IsEqualTo(AppliedEventState.Pending)
      .Because("the collective sink's work is outstanding, and it applies to every model the event targets");

    await coordinator.CompletePerspectiveAsync(
      cursors: [], eventWorkIds: [await _workIdAsync(conn, eventId, CollectiveRouting.SINK_PERSPECTIVE_NAME)], debugMode: false);

    await Assert.That((await coordinator.GetAppliedEventStatusAsync(inquiry))!.Value.State)
      .IsEqualTo(AppliedEventState.Applied);
  }

  [Test]
  public async Task AStoredEventThePerspectiveDoesNotHandle_IsNotApplicableAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var conn = await _openAsync(ctx);
    var eventId = Guid.CreateVersion7();

    await coordinator.StoreInboxMessagesAsync([_fromAnotherService(eventId, Guid.CreateVersion7())], partitionCount: 100);
    await _claimAsync(conn);

    var status = await coordinator.GetAppliedEventStatusAsync(new AppliedEventInquiry(PERSPECTIVE, eventId));
    await Assert.That(status!.Value.State).IsEqualTo(AppliedEventState.NotApplicable)
      .Because("no association routes the event to the perspective, so nothing will ever apply it");
    await Assert.That(status.Value.EventId).IsEqualTo(eventId);
  }

  [Test]
  public async Task ByStreamAndPosition_ResolvesTheLocalVersionAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var conn = await _openAsync(ctx);
    await _associateAsync(conn);
    var eventId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var byPosition = new AppliedEventInquiry(PERSPECTIVE, null, streamId, 1);

    var before = await coordinator.GetAppliedEventStatusAsync(byPosition);
    await Assert.That(before!.Value.State).IsEqualTo(AppliedEventState.NotArrived);
    await Assert.That(before.Value.EventId).IsNull();

    await coordinator.StoreInboxMessagesAsync([_fromAnotherService(eventId, streamId)], partitionCount: 100);
    await _claimAsync(conn);

    var after = await coordinator.GetAppliedEventStatusAsync(byPosition);
    await Assert.That(after!.Value.State).IsEqualTo(AppliedEventState.Pending);
    await Assert.That(after.Value.EventId).IsEqualTo(eventId)
      .Because("the first event of the stream is at position 1 in this service's event store");
  }

  [Test]
  public async Task DebugModeCompletion_RecordsTheApplyOnce_AndARetainedRowIsNotPendingAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var conn = await _openAsync(ctx);
    await _associateAsync(conn);
    var eventId = Guid.CreateVersion7();
    await coordinator.StoreInboxMessagesAsync([_fromAnotherService(eventId, Guid.CreateVersion7())], partitionCount: 100);
    await _claimAsync(conn);
    var workId = await _workIdAsync(conn, eventId, PERSPECTIVE);

    await coordinator.CompletePerspectiveAsync(cursors: [], eventWorkIds: [workId], debugMode: true);
    await coordinator.CompletePerspectiveAsync(cursors: [], eventWorkIds: [workId], debugMode: true);

    await Assert.That(await _ledgerCountAsync(conn, eventId)).IsEqualTo(1L)
      .Because("a completion delivered twice records the apply once");
    var status = await coordinator.GetAppliedEventStatusAsync(new AppliedEventInquiry(PERSPECTIVE, eventId));
    await Assert.That(status!.Value.State).IsEqualTo(AppliedEventState.Applied);
  }

  [Test]
  public async Task ACompletion_PrunesLedgerRowsOlderThanAnHourAsync() {
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);
    var conn = await _openAsync(ctx);
    var stale = Guid.CreateVersion7();
    var fresh = Guid.CreateVersion7();
    await using (var seed = conn.CreateCommand()) {
      seed.CommandText = @"
        INSERT INTO wh_perspective_applied (event_id, perspective_name, stream_id, applied_at) VALUES
          (@stale, @p, gen_random_uuid(), NOW() - INTERVAL '2 hours'),
          (@fresh, @p, gen_random_uuid(), NOW() - INTERVAL '10 minutes')";
      seed.Parameters.AddWithValue("stale", stale);
      seed.Parameters.AddWithValue("fresh", fresh);
      seed.Parameters.AddWithValue("p", PERSPECTIVE);
      await seed.ExecuteNonQueryAsync();
    }
    await _associateAsync(conn);
    var eventId = Guid.CreateVersion7();
    await coordinator.StoreInboxMessagesAsync([_fromAnotherService(eventId, Guid.CreateVersion7())], partitionCount: 100);
    await _claimAsync(conn);

    await coordinator.CompletePerspectiveAsync(cursors: [], eventWorkIds: [await _workIdAsync(conn, eventId, PERSPECTIVE)], debugMode: false);

    await Assert.That(await _ledgerCountAsync(conn, stale)).IsEqualTo(0L);
    await Assert.That(await _ledgerCountAsync(conn, fresh)).IsEqualTo(1L);
    await Assert.That(await _ledgerCountAsync(conn, eventId)).IsEqualTo(1L);
  }
}
