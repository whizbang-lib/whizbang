// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Events used only by <see cref="EventStoreReadPolymorphicTests"/>. The nested container mirrors the
/// shape a consumer gets when events are grouped in a static contract class, which is what makes the
/// stored type name a nested one (<c>Namespace.Container+Event</c>) rather than a flat one.
/// </summary>
public static class ReadProbeContracts {
  public record ProbeAlphaEvent : IEvent {
    [StreamId]
    public required Guid StreamId { get; init; }
    public required int Ordinal { get; init; }
  }

  public record ProbeBetaEvent : IEvent {
    [StreamId]
    public required Guid StreamId { get; init; }
    public required int Ordinal { get; init; }
  }
}

/// <summary>A second container holding an event with the SAME simple name as one above.</summary>
public static class ReadProbeOtherContracts {
  public record ProbeAlphaEvent : IEvent {
    [StreamId]
    public required Guid StreamId { get; init; }
    public required int Ordinal { get; init; }
  }
}

/// <summary>An event the reads below never ask for.</summary>
public record ProbeUnrequestedEvent : IEvent {
  [StreamId]
  public required Guid StreamId { get; init; }
}

/// <summary>
/// The read seam itself: <c>ReadPolymorphicAsync</c> is what every replay and rebuild folds from, and
/// nothing exercised it directly. A read that returns fewer events than the stream holds is
/// indistinguishable, from the rebuilder's side, from a rebuild that correctly decided to do nothing —
/// so these assert the read's own contract rather than a rebuild's outcome.
/// </summary>
[Category("Integration")]
[Category("Shard5")]
public class EventStoreReadPolymorphicTests : EFCoreTestBase {

  private static readonly Type[] BothProbeTypes = [
    typeof(ReadProbeContracts.ProbeAlphaEvent),
    typeof(ReadProbeContracts.ProbeBetaEvent),
  ];

  private static readonly int[] ReverseCommitOrder = [5, 4, 3, 2, 1, 0];

  private static async Task<Guid> _appendAsync<TEvent>(
      EFCoreEventStore<WorkCoordinationDbContext> store, Guid streamId, TEvent payload,
      string? tenantId = null, string? userId = null)
      where TEvent : IEvent {
    var messageId = MessageId.New();
    var hop = new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UtcNow,
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Scope = tenantId is null && userId is null
          ? null
          : ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = tenantId, UserId = userId }),
    };
    await store.AppendAsync(streamId, new MessageEnvelope<TEvent> {
      MessageId = messageId,
      Payload = payload,
      Hops = [hop],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    });
    return messageId.Value;
  }

  private static async Task<List<MessageEnvelope<IEvent>>> _readAllAsync(
      EFCoreEventStore<WorkCoordinationDbContext> store, Guid streamId, Guid? fromEventId, IReadOnlyList<Type> types) {
    var read = new List<MessageEnvelope<IEvent>>();
    await foreach (var envelope in store.ReadPolymorphicAsync(streamId, fromEventId, types, CancellationToken.None)) {
      read.Add(envelope);
    }
    return read;
  }

  // ── The basic contract ──────────────────────────────────────────────────────────────────────

  [Test]
  public async Task ReadPolymorphicAsync_ReturnsEveryEventOnTheStreamAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    for (var i = 0; i < 12; i++) {
      await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = i });
    }

    var read = await _readAllAsync(store, streamId, null, BothProbeTypes);

    await Assert.That(read).Count().IsEqualTo(12)
      .Because("a replay folds what this returns, so a short read is a silently short replay");
  }

  [Test]
  public async Task ReadPolymorphicAsync_ReturnsNothingForAnUnknownStreamAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var read = await _readAllAsync(store, Guid.NewGuid(), null, BothProbeTypes);

    await Assert.That(read).IsEmpty();
  }

  [Test]
  public async Task ReadPolymorphicAsync_DoesNotLeakEventsFromAnotherStreamAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var mine = Guid.NewGuid();
    var theirs = Guid.NewGuid();

    await _appendAsync(store, mine, new ReadProbeContracts.ProbeAlphaEvent { StreamId = mine, Ordinal = 1 });
    await _appendAsync(store, theirs, new ReadProbeContracts.ProbeAlphaEvent { StreamId = theirs, Ordinal = 2 });

    var read = await _readAllAsync(store, mine, null, BothProbeTypes);

    await Assert.That(read).Count().IsEqualTo(1);
  }

  // ── Type resolution: the storage form, nesting, and scale ───────────────────────────────────

  [Test]
  public async Task ReadPolymorphicAsync_ResolvesANestedTypeNameAsync() {
    // The stored name is "Namespace.Container+Event, Assembly". Resolution against that exact form is
    // what a consumer's grouped contract classes depend on.
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 1 });

    var stored = await context.Set<EventStoreRecord>().AsNoTracking().SingleAsync(e => e.StreamId == streamId);
    await Assert.That(stored.EventType).Contains("+")
      .Because("the probe events are nested, so the stored form must be the nested one these tests are about");

    var read = await _readAllAsync(store, streamId, null, BothProbeTypes);
    await Assert.That(read).Count().IsEqualTo(1);
  }

  [Test]
  public async Task ReadPolymorphicAsync_SkipsATypeTheCallerDidNotAskForAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 1 });
    await _appendAsync(store, streamId, new ProbeUnrequestedEvent { StreamId = streamId });

    var read = await _readAllAsync(store, streamId, null, [typeof(ReadProbeContracts.ProbeAlphaEvent)]);

    await Assert.That(read).Count().IsEqualTo(1)
      .Because("a perspective materializes only its own registered types");
  }

  [Test]
  public async Task ReadPolymorphicAsync_ResolvesTwoTypesSharingASimpleNameAsync() {
    // BuildTypeLookup registers a simple-name fallback last-writer-wins, so two containers holding an
    // event of the same name collide on that key. The qualified key must still carry both.
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 1 });
    await _appendAsync(store, streamId, new ReadProbeOtherContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 2 });

    var read = await _readAllAsync(store, streamId, null, [
      typeof(ReadProbeContracts.ProbeAlphaEvent),
      typeof(ReadProbeOtherContracts.ProbeAlphaEvent),
    ]);

    await Assert.That(read).Count().IsEqualTo(2)
      .Because("the simple-name collision must not cost either type its resolution");
    await Assert.That(read.Select(e => e.Payload.GetType()).Distinct().Count()).IsEqualTo(2)
      .Because("each event deserializes to its own container's type, not twice to the surviving one");
  }

  [Test]
  public async Task ReadPolymorphicAsync_ResolvesAgainstALargeRequestedTypeListAsync() {
    // A real perspective can subscribe to a hundred event types; the probe perspectives subscribe to a
    // handful, so nothing covered the lookup at that size.
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 1 });
    await _appendAsync(store, streamId, new ReadProbeContracts.ProbeBetaEvent { StreamId = streamId, Ordinal = 2 });

    // Pad the requested list with every other event type in this assembly, so the lookup is built at a
    // realistic size rather than at two.
    var padding = typeof(ReadProbeContracts).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IEvent).IsAssignableFrom(t))
        .ToArray();
    var requested = padding.Concat(BothProbeTypes).Distinct().ToArray();

    await Assert.That(requested.Length).IsGreaterThan(10)
      .Because("the point of this test is the size of the lookup");

    var read = await _readAllAsync(store, streamId, null, requested);

    await Assert.That(read).Count().IsEqualTo(2);
  }

  // ── Ordering and the fromEventId anchor ─────────────────────────────────────────────────────

  [Test]
  public async Task ReadPolymorphicAsync_ReturnsEventsInCommitOrderAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    for (var i = 0; i < 6; i++) {
      await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = i });
    }
    // Stamp them in the REVERSE of append order, so an id-ordered read and a commit-ordered read differ.
    await context.Database.ExecuteSqlRawAsync(@"
      UPDATE wh_event_store e SET commit_sequence = x.seq
        FROM (SELECT event_id, row_number() OVER (ORDER BY event_id DESC) AS seq
                FROM wh_event_store WHERE stream_id = {0}) x
       WHERE e.event_id = x.event_id", streamId);

    var read = await _readAllAsync(store, streamId, null, BothProbeTypes);

    var ordinals = read.Select(e => ((ReadProbeContracts.ProbeAlphaEvent)e.Payload).Ordinal).ToList();
    await Assert.That(ordinals).IsEquivalentTo(ReverseCommitOrder)
      .Because("the read orders by commit sequence, which is what makes a replay deterministic");
  }

  [Test]
  public async Task ReadPolymorphicAsync_ReturnsUnstampedEventsAtTheTailAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    // Address the rows by the ids the appends returned: version numbering is the store's business and
    // an assumption about where it starts is exactly how this test was wrong the first time.
    var unstamped = await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 0 });
    var stamped = await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 1 });
    await context.Database.ExecuteSqlRawAsync(
      "UPDATE wh_event_store SET commit_sequence = NULL WHERE event_id = {0}", unstamped);
    await context.Database.ExecuteSqlRawAsync(
      "UPDATE wh_event_store SET commit_sequence = 9999 WHERE event_id = {0}", stamped);

    var read = await _readAllAsync(store, streamId, null, BothProbeTypes);

    await Assert.That(read).Count().IsEqualTo(2)
      .Because("an unstamped event is still history; the stamper lagging must not hide it from a replay");
    await Assert.That(((ReadProbeContracts.ProbeAlphaEvent)read[^1].Payload).Ordinal).IsEqualTo(0)
      .Because("NULLS LAST puts the unstamped row at the tail");
  }

  [Test]
  public async Task ReadPolymorphicAsync_FromAnEventId_ExcludesThatEventAndEverythingBelowItAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    var first = await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 0 });
    await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 1 });
    await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 2 });

    var read = await _readAllAsync(store, streamId, first, BothProbeTypes);

    await Assert.That(read).Count().IsEqualTo(2)
      .Because("the anchor is exclusive — it is the last event already applied");
  }

  [Test]
  public async Task ReadPolymorphicAsync_FromTheHighestEventId_ReturnsNothingAsync() {
    // The anchor filters by ID comparison, not by commit order. Anchoring on the largest id therefore
    // empties the read even though the stream is full — the shape that makes a resumed replay a no-op.
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    var ids = new List<Guid>();
    for (var i = 0; i < 5; i++) {
      ids.Add(await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = i }));
    }
    var highest = ids.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).Last();

    var read = await _readAllAsync(store, streamId, highest, BothProbeTypes);

    await Assert.That(read).IsEmpty()
      .Because("documents the anchor's id-order semantics: a high anchor hides later-committed events");
  }

  // ── The envelope the read reconstructs ──────────────────────────────────────────────────────

  [Test]
  public async Task ReadPolymorphicAsync_CarriesTheLocalCommitSequenceAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 1 });
    await context.Database.ExecuteSqlRawAsync(
      "UPDATE wh_event_store SET commit_sequence = 4242 WHERE stream_id = {0}", streamId);

    var read = await _readAllAsync(store, streamId, null, BothProbeTypes);

    await Assert.That(read[0].LocalCommitSequence).IsEqualTo(4242L)
      .Because("the idempotency filter compares against this; dropping it silently re-applies late events");
  }

  [Test]
  public async Task ReadPolymorphicAsync_RestoresTheScopeIntoTheHopsAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 1 },
        tenantId: "tenant-probe", userId: "user-probe");

    var read = await _readAllAsync(store, streamId, null, BothProbeTypes);

    var scope = read[0].GetCurrentScope();
    await Assert.That(scope).IsNotNull()
      .Because("the runner reads the row's scope from the hops when it writes the perspective row");
    await Assert.That(scope!.Scope.TenantId).IsEqualTo("tenant-probe");
    await Assert.That(scope.Scope.UserId).IsEqualTo("user-probe");
  }

  [Test]
  public async Task ReadPolymorphicAsync_PreservesTheMessageIdAsync() {
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    var appended = await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 1 });

    var read = await _readAllAsync(store, streamId, null, BothProbeTypes);

    await Assert.That(read[0].MessageId.Value).IsEqualTo(appended)
      .Because("the checkpoint is this id; a different one checkpoints the wrong position");
  }

  // ── The silent skips ────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task ReadPolymorphicAsync_SkipsAnEventWhoseBodyIsGoneAsync() {
    // A pointer with no body row is skipped with a bare `continue`. That is deliberate for a reaped
    // ephemeral body, and it is also the one way a read can come back short with nothing logged.
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 0 });
    await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = 1 });
    await context.Database.ExecuteSqlRawAsync(@"
      DELETE FROM wh_event_body WHERE event_id IN
        (SELECT event_id FROM wh_event_store WHERE stream_id = {0} AND version = 1)", streamId);

    var read = await _readAllAsync(store, streamId, null, BothProbeTypes);

    await Assert.That(read).Count().IsEqualTo(1)
      .Because("a body-less pointer is skipped, and nothing in the read says so — pinned so a change here is deliberate");
  }

  [Test]
  public async Task ReadPolymorphicAsync_ReturnsNothingWhenEveryBodyIsGoneAsync() {
    // The whole-stream version of the skip above: a full stream reads as empty. A rebuild folding this
    // applies no events, writes no row, and still reports the stream processed.
    await using var context = CreateDbContext();
    var store = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var streamId = Guid.NewGuid();

    for (var i = 0; i < 4; i++) {
      await _appendAsync(store, streamId, new ReadProbeContracts.ProbeAlphaEvent { StreamId = streamId, Ordinal = i });
    }
    await context.Database.ExecuteSqlRawAsync(@"
      DELETE FROM wh_event_body WHERE event_id IN
        (SELECT event_id FROM wh_event_store WHERE stream_id = {0})", streamId);

    var read = await _readAllAsync(store, streamId, null, BothProbeTypes);

    await Assert.That(read).IsEmpty()
      .Because("records the exact shape a silently-skipped rebuild would have: a full stream that reads as empty");
  }
}
