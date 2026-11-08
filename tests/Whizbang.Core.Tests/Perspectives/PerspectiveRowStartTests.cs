// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// The start a perspective row is dated at when it is written without the event that began its stream (#2152): asked of
/// the event store only for a row that may be inserted and a fold that does not begin with the stream's first event, and
/// carried to the store's write for that one row.
/// </summary>
/// <docs>fundamentals/events/superseding-events#what-a-rebuild-keeps</docs>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRowStart.cs</code-under-test>
[Category("Unit")]
[Category("Perspectives")]
public class PerspectiveRowStartTests {
  private static readonly DateTime _start = new(2019, 1, 2, 3, 4, 5, DateTimeKind.Utc);
  private static readonly DateTimeOffset _mar1 = new(2020, 3, 1, 9, 0, 0, TimeSpan.Zero);
  private static readonly DateTimeOffset _jun5 = new(2020, 6, 5, 9, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task For_WithNothingBegun_IsNullAsync() {
    await Assert.That(PerspectiveRowStart.For(Guid.NewGuid())).IsNull();
  }

  [Test]
  public async Task Begin_DatesThatRowOnly_UntilTheHandleIsDisposedAsync() {
    var row = Guid.NewGuid();
    using (PerspectiveRowStart.Begin(row, _start)) {
      await Assert.That(PerspectiveRowStart.For(row)).IsEqualTo(_start);
      await Assert.That(PerspectiveRowStart.For(Guid.NewGuid())).IsNull()
        .Because("a start belongs to the row it was resolved for; another row this flow writes keeps its own date");
    }
    await Assert.That(PerspectiveRowStart.For(row)).IsNull();
  }

  [Test]
  public async Task Begin_WithNoStart_ChangesNothingAsync() {
    var row = Guid.NewGuid();
    using (PerspectiveRowStart.Begin(row, _start)) {
      using (PerspectiveRowStart.Begin(row, null)) {
        await Assert.That(PerspectiveRowStart.For(row)).IsEqualTo(_start)
          .Because("nothing to date the row at leaves the flow as it was");
      }
      await Assert.That(PerspectiveRowStart.For(row)).IsEqualTo(_start);
    }
  }

  [Test]
  public async Task Begin_Nested_RestoresTheOuterStartOnDisposeAsync() {
    var outer = Guid.NewGuid();
    var inner = Guid.NewGuid();
    using (PerspectiveRowStart.Begin(outer, _start)) {
      using (PerspectiveRowStart.Begin(inner, _start.AddDays(1))) {
        await Assert.That(PerspectiveRowStart.For(inner)).IsEqualTo(_start.AddDays(1));
        await Assert.That(PerspectiveRowStart.For(outer)).IsNull()
          .Because("the innermost start is the one the flow's write uses");
      }
      await Assert.That(PerspectiveRowStart.For(outer)).IsEqualTo(_start);
    }
  }

  [Test]
  public async Task ResolveAsync_ARowThatExists_AsksNothingAsync() {
    var store = new StartStore(_start);
    var row = Guid.NewGuid();

    var resolved = await PerspectiveRowStart.ResolveAsync(store, row, rowExists: true, [_event(row, first: false, _jun5)]);

    await Assert.That(resolved).IsNull();
    await Assert.That(store.Asked).IsEqualTo(0)
      .Because("an existing row keeps its created_at on update, so nothing dates it");
  }

  [Test]
  public async Task ResolveAsync_AFoldThatBeginsTheStream_IsTheFirstEventsTime_NotTheLastsAsync() {
    var store = new StartStore(_start);
    var row = Guid.NewGuid();

    var resolved = await PerspectiveRowStart.ResolveAsync(
      store, row, rowExists: false, [_event(row, first: true, _mar1), _event(row, first: false, _jun5)]);

    await Assert.That(resolved).IsEqualTo(_mar1.UtcDateTime)
      .Because("#2154: a row is dated at the first event folded into it, whatever else its first write folds; the "
        + "last event's time is updated_at");
    await Assert.That(resolved).IsNotEqualTo(_jun5.UtcDateTime);
    await Assert.That(store.Asked).IsEqualTo(0)
      .Because("a fold that holds the stream's first event needs no round trip");
  }

  [Test]
  public async Task ResolveAsync_AFoldStartingAfterTheFirstEvent_TakesTheStoresStartAsUtcAsync() {
    var store = new StartStore(DateTime.SpecifyKind(_start, DateTimeKind.Unspecified));
    var row = Guid.NewGuid();

    var resolved = await PerspectiveRowStart.ResolveAsync(store, row, rowExists: false, [_event(row, first: false, _jun5)]);

    await Assert.That(resolved).IsEqualTo(_start)
      .Because("#2152: the stream's first event is gone, so the row is dated at the stream's recorded start");
    await Assert.That(resolved!.Value.Kind).IsEqualTo(DateTimeKind.Utc);
    await Assert.That(store.Asked).IsEqualTo(1);
    await Assert.That(store.AskedFor).IsEqualTo(row);
  }

  [Test]
  public async Task ResolveAsync_AStreamThatStillHoldsItsStart_IsTheFirstFoldedEventsTimeAsync() {
    var store = new StartStore(null);
    var row = Guid.NewGuid();

    var resolved = await PerspectiveRowStart.ResolveAsync(
      store, row, rowExists: false, [_event(row, first: false, _mar1), _event(row, first: false, _jun5)]);

    await Assert.That(resolved).IsEqualTo(_mar1.UtcDateTime)
      .Because("the store answers null while the stream holds its first event (this perspective folds none of the "
        + "events before), so the row is dated at the first event it folded");
    await Assert.That(store.Asked).IsEqualTo(1);
  }

  [Test]
  public async Task ResolveAsync_AStoreThatRemovesNoEvents_DatesByTheFirstFoldedEventAsync() {
    // The interface default: a store that never removes a stream's first event has no start apart from its events.
    var row = Guid.NewGuid();

    var resolved = await PerspectiveRowStart.ResolveAsync(new InMemoryEventStore(), row, rowExists: false, [_event(row, first: false, _mar1)]);

    await Assert.That(resolved).IsEqualTo(_mar1.UtcDateTime);
  }

  [Test]
  public async Task ResolveAsync_AFirstEventWithNoHop_LeavesTheRowToTheWritesOwnTimeAsync() {
    var row = Guid.NewGuid();

    var resolved = await PerspectiveRowStart.ResolveAsync(new StartStore(null), row, rowExists: false, [_event(row, first: true, null)]);

    await Assert.That(resolved).IsNull()
      .Because("an event with no hop carries no business time, so the write keeps the time it was given");
  }

  [Test]
  public async Task ResolveAsync_AFirstEventReadWithNoHopList_LeavesTheRowToTheWritesOwnTimeAsync() {
    // A stored envelope can come back with no hop list at all; the runner reads it the same way (no business time).
    var row = Guid.NewGuid();
    var envelope = _event(row, first: true, null);
    var hopless = new MessageEnvelope<IEvent> {
      MessageId = envelope.MessageId,
      Payload = envelope.Payload,
      Hops = null!,
      DispatchContext = envelope.DispatchContext,
      IsFirstInLocalStream = true,
    };

    await Assert.That(await PerspectiveRowStart.ResolveAsync(new StartStore(null), row, rowExists: false, [hopless])).IsNull();
  }

  [Test]
  public async Task ResolveAsync_CollectivesAheadOfTheFirstStreamEvent_AreSkippedAsync() {
    var store = new StartStore(_start);
    var row = Guid.NewGuid();
    MessageEnvelope<IEvent>[] folded = [_collective(), _event(row, first: true, _jun5)];

    var resolved = await PerspectiveRowStart.ResolveAsync(store, row, rowExists: false, folded);

    await Assert.That(resolved).IsEqualTo(_jun5.UtcDateTime)
      .Because("a collective is another stream's event; the row's own first event dates it and decides whether the fold "
        + "begins the stream");
    await Assert.That(store.Asked).IsEqualTo(0);
  }

  [Test]
  public async Task ResolveAsync_NoStreamEventFolded_AsksNothingAsync() {
    var store = new StartStore(_start);

    var resolved = await PerspectiveRowStart.ResolveAsync(store, Guid.NewGuid(), rowExists: false, [_collective()]);

    await Assert.That(resolved).IsNull();
    await Assert.That(store.Asked).IsEqualTo(0);
  }

  [Test]
  public async Task ResolveAsync_NullArguments_ThrowAsync() {
    await Assert.That(() => PerspectiveRowStart.ResolveAsync(null!, Guid.NewGuid(), false, [])).Throws<ArgumentNullException>();
    await Assert.That(() => PerspectiveRowStart.ResolveAsync(new StartStore(null), Guid.NewGuid(), false, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Begin_ThenAwait_TheStartFlowsIntoTheCallsItMakesAsync() {
    var row = Guid.NewGuid();
    using var _ = PerspectiveRowStart.Begin(row, _start);

    var seenInACall = await _readInAnotherCallAsync(row);

    await Assert.That(seenInACall).IsEqualTo(_start)
      .Because("the runner begins the start and then awaits the store's write, which must see it");
  }

  private static async Task<DateTime?> _readInAnotherCallAsync(Guid row) {
    await Task.Yield();
    return PerspectiveRowStart.For(row);
  }

  private static MessageEnvelope<IEvent> _event(Guid stream, bool first, DateTimeOffset? at) => new() {
    MessageId = MessageId.New(),
    Payload = new RowEvent { StreamId = stream },
    Hops = at is { } ts
      ? [new MessageHop {
          Type = HopType.Current,
          Timestamp = ts.AddDays(-1),
          ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.Empty, ServiceName = "producer", HostName = "h", ProcessId = 1 },
        }, new MessageHop {
          Type = HopType.Current,
          Timestamp = ts,
          ServiceInstance = new ServiceInstanceInfo { InstanceId = Guid.Empty, ServiceName = "store", HostName = "h", ProcessId = 1 },
        }]
      : [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    IsFirstInLocalStream = first,
  };

  private static MessageEnvelope<IEvent> _collective() => new() {
    MessageId = MessageId.New(),
    Payload = new RowsGranted(),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    IsFirstInLocalStream = false,
  };

  /// <summary>An event of the row's stream.</summary>
  public sealed record RowEvent : IEvent {
    /// <summary>The stream.</summary>
    [StreamId]
    public Guid StreamId { get; init; }
  }

  /// <summary>A collective, another stream's event folded into the row.</summary>
  private sealed record RowsGranted : ICollectiveEvent {
    public CollectiveScope Scope { get; init; } = new TenantCollectiveScope("tenant-a");
  }

  /// <summary>An in-memory store that answers a fixed start and counts the questions.</summary>
  private sealed class StartStore(DateTime? start) : InMemoryEventStore, IEventStore {
    public int Asked { get; private set; }
    public Guid AskedFor { get; private set; }

    public Task<DateTime?> GetStreamStartedAtAsync(Guid streamId, CancellationToken cancellationToken = default) {
      Asked++;
      AskedFor = streamId;
      return Task.FromResult(start);
    }
  }
}
