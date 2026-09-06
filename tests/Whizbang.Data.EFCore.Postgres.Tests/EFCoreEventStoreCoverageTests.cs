using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage-round-23 targeted tests for <see cref="EFCoreEventStore{TDbContext}"/>: the
/// duplicate-key wrapping in <c>_appendCoreAsync</c>, and the reaped-ephemeral-body skip on the
/// three body-aware read paths <see cref="EphemeralEventStoreReadTests"/> does not cover
/// (<see cref="EFCoreEventStore{TDbContext}.ReadAsync{TMessage}(System.Guid,long,System.Threading.CancellationToken)"/>,
/// the by-event-id overload, <c>GetEventsBetweenAsync</c>, and <c>GetEventsBetweenPolymorphicAsync</c>
/// via <c>_getEventsBetweenPolymorphicCoreAsync</c> — <c>ReadPolymorphicAsync</c>'s own reaped-skip is
/// already covered there). Requires a live Postgres: every target line sits inside an EF Core
/// LINQ-to-SQL query (the body-aware LEFT JOIN, or a real unique-constraint violation), which no
/// pure-logic fake can exercise.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs</code-under-test>
[Category("Shard1")]
public class EFCoreEventStoreCoverageTests : EFCoreTestBase {

  private static MessageHop _testHop() => new() {
    Type = HopType.Current,
    Timestamp = DateTime.UtcNow,
    ServiceInstance = new ServiceInstanceInfo {
      InstanceId = Guid.NewGuid(),
      ServiceName = "test-service",
      HostName = "test-host",
      ProcessId = 123
    }
  };

  private static async Task<MessageEnvelope<OrderCreatedEvent>> _appendOrderAsync(
      EFCoreEventStore<WorkCoordinationDbContext> eventStore, Guid streamId, string customerName) {
    var envelope = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent { OrderId = Guid.NewGuid(), CustomerName = customerName },
      Hops = [_testHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    await eventStore.AppendAsync(streamId, envelope);
    return envelope;
  }

  /// <summary>
  /// If this wrapping regressed, a raw <see cref="DbUpdateException"/> would leak out of
  /// <c>AppendAsync</c> on any unique-constraint collision, breaking every caller that catches
  /// <see cref="InvalidOperationException"/> specifically to detect "someone already appended this"
  /// — the optimistic-concurrency contract <c>AppendAsync</c>'s own doc comment documents.
  /// </summary>
  [Test]
  public async Task AppendAsync_DuplicateEventId_WrapsDbUpdateExceptionAsInvalidOperationAsync() {
    var streamId = Guid.NewGuid();
    var duplicateId = (Guid)TrackedGuid.NewMedo();   // MessageId.From requires UUIDv7

    // Seed a pointer row that already occupies this event_id (the primary key) on a SEPARATE
    // context/connection -- a deterministic stand-in for two processes racing to append the same
    // MessageId, without needing an actual race. (Using the SAME context the eventStore under test
    // uses would make EF's own change-tracker reject the second Add before any SQL runs at all,
    // which would prove nothing about the catch block under test.)
    await using (var seedContext = CreateDbContext()) {
      seedContext.Set<EventStoreRecord>().Add(new EventStoreRecord {
        Id = duplicateId,
        StreamId = streamId,
        AggregateId = streamId,
        AggregateType = "TestAggregate",
        Version = 0,
        EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
        EventData = null,
        Metadata = null,
        CreatedAt = DateTime.UtcNow
      });
      await seedContext.SaveChangesAsync();
    }

    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var envelope = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.From(duplicateId),
      Payload = new OrderCreatedEvent { OrderId = Guid.NewGuid(), CustomerName = "Duplicate" },
      Hops = [_testHop()],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    InvalidOperationException? caught = null;
    try {
      await eventStore.AppendAsync(streamId, envelope);
    } catch (InvalidOperationException ex) {
      caught = ex;
    }

    await Assert.That(caught).IsNotNull()
      .Because("a unique-constraint violation on append must surface as InvalidOperationException, " +
               "never the raw DbUpdateException/PostgresException.");
    await Assert.That(caught!.Message).Contains(streamId.ToString())
      .Because("the message must name the stream the caller was appending to.");
    await Assert.That(caught.Message).Contains("Another process has already appended to this stream")
      .Because("the message must say WHY this looks like a race, not just that something failed.");
    await Assert.That(caught.InnerException).IsNotNull()
      .Because("the original DbUpdateException must be preserved as the inner exception, not " +
               "swallowed -- it carries the actual Postgres diagnostics.");
  }

  /// <summary>
  /// If this skip regressed to a throw, a single reaped (consumed, snapshot-covered) row anywhere
  /// in a stream would abort the ENTIRE sequence-ordered read mid-iteration — every event after it
  /// would silently never reach the caller, not just the reaped one.
  /// </summary>
  [Test]
  public async Task ReadAsync_BySequence_SkipsReapedEphemeralBodyAsync() {
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    var reaped = await _appendOrderAsync(eventStore, streamId, "Reaped");
    await _appendOrderAsync(eventStore, streamId, "Alive");

    // Simulate a tier-1 reap: the pointer row survives, its offloaded body does not.
    var reapedBody = await context.Set<EventBodyRecord>().SingleAsync(b => b.EventId == reaped.MessageId.Value);
    context.Set<EventBodyRecord>().Remove(reapedBody);
    await context.SaveChangesAsync();

    var events = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var evt in eventStore.ReadAsync<OrderCreatedEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }

    await Assert.That(events.Count).IsEqualTo(1)
      .Because("the reaped pointer-only row must be skipped, not thrown on.");
    await Assert.That(events[0].Payload.CustomerName).IsEqualTo("Alive")
      .Because("the alive event that comes after the reaped one in the stream must still be " +
               "returned, in its original order.");
  }

  /// <summary>
  /// Same invariant as the by-sequence overload, exercised on the by-event-id overload that
  /// rewind/perspective catch-up actually calls. If ITS skip regressed independently, a reaped row
  /// would abort this overload specifically even with the by-sequence overload fixed.
  /// </summary>
  [Test]
  public async Task ReadAsync_ByEventId_SkipsReapedEphemeralBodyAsync() {
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    var reaped = await _appendOrderAsync(eventStore, streamId, "Reaped");
    await _appendOrderAsync(eventStore, streamId, "Alive");

    var reapedBody = await context.Set<EventBodyRecord>().SingleAsync(b => b.EventId == reaped.MessageId.Value);
    context.Set<EventBodyRecord>().Remove(reapedBody);
    await context.SaveChangesAsync();

    var events = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var evt in eventStore.ReadAsync<OrderCreatedEvent>(streamId, fromEventId: null)) {
      events.Add(evt);
    }

    await Assert.That(events.Count).IsEqualTo(1)
      .Because("the reaped pointer-only row must be skipped, not thrown on.");
    await Assert.That(events[0].Payload.CustomerName).IsEqualTo("Alive");
  }

  /// <summary>
  /// Lifecycle receptors load just-processed events via <c>GetEventsBetweenAsync</c>. If this skip
  /// regressed, a reap racing a lifecycle read would throw mid-batch instead of yielding the
  /// still-alive events, stalling PostLifecycle processing for the whole batch rather than just
  /// the one reaped row.
  /// </summary>
  [Test]
  public async Task GetEventsBetweenAsync_SkipsReapedEphemeralBodyAsync() {
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    var reaped = await _appendOrderAsync(eventStore, streamId, "Reaped");
    await _appendOrderAsync(eventStore, streamId, "Alive");

    var reapedBody = await context.Set<EventBodyRecord>().SingleAsync(b => b.EventId == reaped.MessageId.Value);
    context.Set<EventBodyRecord>().Remove(reapedBody);
    await context.SaveChangesAsync();

    var events = await eventStore.GetEventsBetweenAsync<OrderCreatedEvent>(
      streamId, afterEventId: null, upToEventId: Guid.Empty, CancellationToken.None);

    await Assert.That(events.Count).IsEqualTo(1)
      .Because("the reaped pointer-only row must be skipped, not thrown on.");
    await Assert.That(events[0].Payload.CustomerName).IsEqualTo("Alive");
  }

  /// <summary>
  /// The polymorphic sibling of <c>GetEventsBetweenAsync</c>, used when a perspective handles
  /// multiple event types. If ITS skip regressed independently, the same reap would abort
  /// multi-type lifecycle catch-up specifically, leaving the single-type path's fix looking
  /// complete while this one still throws.
  /// </summary>
  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_SkipsReapedEphemeralBodyAsync() {
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    var reaped = await _appendOrderAsync(eventStore, streamId, "Reaped");
    await _appendOrderAsync(eventStore, streamId, "Alive");

    var reapedBody = await context.Set<EventBodyRecord>().SingleAsync(b => b.EventId == reaped.MessageId.Value);
    context.Set<EventBodyRecord>().Remove(reapedBody);
    await context.SaveChangesAsync();

    var eventTypes = new List<Type> { typeof(OrderCreatedEvent) };
    var events = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId, afterEventId: null, upToEventId: Guid.Empty, eventTypes, CancellationToken.None);

    await Assert.That(events.Count).IsEqualTo(1)
      .Because("the reaped pointer-only row must be skipped, not thrown on.");
    await Assert.That(((OrderCreatedEvent)events[0].Payload).CustomerName).IsEqualTo("Alive");
  }
}
