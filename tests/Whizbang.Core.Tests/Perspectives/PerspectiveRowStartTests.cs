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
  private static readonly DateTime _start = new(2020, 3, 1, 9, 0, 0, DateTimeKind.Utc);

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

    var resolved = await PerspectiveRowStart.ResolveAsync(store, row, rowExists: true, [_event(row, first: false)]);

    await Assert.That(resolved).IsNull();
    await Assert.That(store.Asked).IsEqualTo(0)
      .Because("an existing row keeps its created_at on update, so its stream's start is never needed");
  }

  [Test]
  public async Task ResolveAsync_AFoldThatBeginsTheStream_AsksNothingAsync() {
    var store = new StartStore(_start);
    var row = Guid.NewGuid();

    var resolved = await PerspectiveRowStart.ResolveAsync(store, row, rowExists: false, [_event(row, first: true), _event(row, first: false)]);

    await Assert.That(resolved).IsNull();
    await Assert.That(store.Asked).IsEqualTo(0)
      .Because("a fold that holds the stream's first event dates the row as it always did, with no extra round trip");
  }

  [Test]
  public async Task ResolveAsync_AFoldStartingAfterTheFirstEvent_TakesTheStoresStartAsUtcAsync() {
    var store = new StartStore(DateTime.SpecifyKind(_start, DateTimeKind.Unspecified));
    var row = Guid.NewGuid();

    var resolved = await PerspectiveRowStart.ResolveAsync(store, row, rowExists: false, [_event(row, first: false)]);

    await Assert.That(resolved).IsEqualTo(_start);
    await Assert.That(resolved!.Value.Kind).IsEqualTo(DateTimeKind.Utc);
    await Assert.That(store.Asked).IsEqualTo(1);
    await Assert.That(store.AskedFor).IsEqualTo(row);
  }

  [Test]
  public async Task ResolveAsync_AStreamThatStillHoldsItsStart_IsNullAsync() {
    var store = new StartStore(null);
    var row = Guid.NewGuid();

    var resolved = await PerspectiveRowStart.ResolveAsync(store, row, rowExists: false, [_event(row, first: false)]);

    await Assert.That(resolved).IsNull()
      .Because("the store answers null while the stream holds its first event, so the row is dated as before");
    await Assert.That(store.Asked).IsEqualTo(1);
  }

  [Test]
  public async Task ResolveAsync_AStoreThatRemovesNoEvents_AnswersNullByDefaultAsync() {
    // The interface default: a store that never removes a stream's first event has no start apart from its events.
    var row = Guid.NewGuid();

    var resolved = await PerspectiveRowStart.ResolveAsync(new InMemoryEventStore(), row, rowExists: false, [_event(row, first: false)]);

    await Assert.That(resolved).IsNull();
  }

  [Test]
  public async Task ResolveAsync_CollectivesAheadOfTheFirstStreamEvent_AreSkippedAsync() {
    var store = new StartStore(_start);
    var row = Guid.NewGuid();
    MessageEnvelope<IEvent>[] folded = [_collective(), _event(row, first: true)];

    var resolved = await PerspectiveRowStart.ResolveAsync(store, row, rowExists: false, folded);

    await Assert.That(resolved).IsNull()
      .Because("a collective is another stream's event; the row's own first event decides whether the fold begins the stream");
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

  private static MessageEnvelope<IEvent> _event(Guid stream, bool first) => new() {
    MessageId = MessageId.New(),
    Payload = new RowEvent { StreamId = stream },
    Hops = [],
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
