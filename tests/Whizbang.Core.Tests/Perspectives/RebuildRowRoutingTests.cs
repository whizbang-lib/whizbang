// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
/// <see cref="RebuildRowRouting"/>: the one place a rebuild decides which row an event folds onto, and the order a row
/// that receives events from several streams folds them in (#1798, #1820). Never by event id: an id records when its
/// producer minted the event, not when the store committed it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/RebuildRowRouting.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/RebuildRekeyLedger.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs</code-under-test>
[Category("Perspectives")]
public class RebuildRowRoutingTests {
  private sealed record Probe(string Marker) : IEvent;

  private static readonly Guid _origin = Guid.Parse("0198f5d2-2222-7000-8000-000000000001");

  [Test]
  public async Task RowOf_OnlyAGenuineRekeyLeavesTheStoredStreamAsync() {
    var stored = Guid.NewGuid();
    var target = Guid.NewGuid();
    var stays = _event("stays");
    var moved = _event("moved");
    var movedToNothing = _event("empty");
    EventRekeys.Record(moved.Payload, target);
    EventRekeys.Record(movedToNothing.Payload, Guid.Empty);

    await Assert.That(RebuildRowRouting.RowOf(stays, stored)).IsEqualTo(stored);
    await Assert.That(RebuildRowRouting.RowOf(moved, stored)).IsEqualTo(target);
    await Assert.That(RebuildRowRouting.RowOf(movedToNothing, stored)).IsEqualTo(stored)
      .Because("an empty stream names no row");
    await Assert.That(() => RebuildRowRouting.RowOf(null!, stored)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Partition_KeepsReadOrderPerRowAsync() {
    var stored = Guid.NewGuid();
    var target = Guid.NewGuid();
    var a = _event("a");
    var b = _event("b");
    var c = _event("c");
    EventRekeys.Record(b.Payload, target);

    var partitions = RebuildRowRouting.Partition(stored, [a, b, c]);

    await Assert.That(partitions.Count).IsEqualTo(2);
    await Assert.That(partitions[0].Row).IsEqualTo(stored);
    await Assert.That(partitions[0].IsRekeyed).IsFalse();
    await Assert.That(partitions[0].Events.Select(_marker).ToList()).IsEquivalentTo(["a", "c"]);
    await Assert.That(partitions[1].Row).IsEqualTo(target);
    await Assert.That(partitions[1].StoredOn).IsEqualTo(stored);
    await Assert.That(partitions[1].IsRekeyed).IsTrue();
    await Assert.That(RebuildRowRouting.Partition(stored, [])).IsEmpty();
    await Assert.That(() => RebuildRowRouting.Partition(stored, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task StaysOn_DropsTheEventsRekeyedAwayAsync() {
    var row = Guid.NewGuid();
    var own = _event("own");
    var away = _event("away");
    EventRekeys.Record(away.Payload, Guid.NewGuid());

    await Assert.That(RebuildRowRouting.StaysOn(row, [own, away]).Select(_marker).ToList()).IsEquivalentTo(["own"]);
    await Assert.That(() => RebuildRowRouting.StaysOn(row, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Merge_InterleavesStreamsByCommitSequenceAsync() {
    // Each stream's own order is kept (versions); across streams the lower commit sequence goes first. The ids are
    // minted in the opposite order on purpose: an id order would fold the stream's last-committed event first.
    var target = new[] { _event("t1", commit: 1, version: 1), _event("t2", commit: 4, version: 2) };
    var source = new[] { _event("p1", commit: 2, version: 1), _event("p2", commit: 5, version: 2) };
    var other = new[] { _event("q1", commit: 3, version: 1) };

    var merged = RebuildRowRouting.Merge([target, source, other, []]);

    await Assert.That(merged.Select(_marker).ToList()).IsEquivalentTo(["t1", "p1", "q1", "t2", "p2"]);
    await Assert.That(ReplayEventOrder.Order(merged)).IsSameReferenceAs(merged)
      .Because("a merged batch is already in fold order; ordering it as one stream would sort versions of different streams");
  }

  [Test]
  public async Task Merge_UnstampedHeadsWaitBehindStampedOnesAsync() {
    var stamped = new[] { _event("s1", commit: 7, version: 1) };
    var unstamped = new[] { _event("u1", commit: null, version: 1) };
    var alsoUnstamped = new[] { _event("v1", commit: null, version: 1) };

    var merged = RebuildRowRouting.Merge([unstamped, stamped, alsoUnstamped]);

    await Assert.That(merged.Select(_marker).ToList()).IsEquivalentTo(["s1", "u1", "v1"])
      .Because("a commit sequence is the only cross-stream order; heads that have none keep the order the streams came in");
  }

  [Test]
  public async Task Merge_SingleOriginKeepsTheOriginsOrderAsync() {
    var first = new[] { _event("o2", commit: 1, version: 1, originSequence: 20) };
    var second = new[] { _event("o1", commit: 2, version: 1, originSequence: 10) };

    var merged = RebuildRowRouting.Merge([first, second]);

    await Assert.That(merged.Select(_marker).ToList()).IsEquivalentTo(["o1", "o2"])
      .Because("every event comes from one origin, whose own sequence is the order it produced them in");
  }

  [Test]
  public async Task Merge_OneOrNoStream_IsThatStreamsOwnReplayOrderAsync() {
    var only = new[] { _event("b", commit: 2, version: 2), _event("a", commit: 1, version: 1) };

    await Assert.That(RebuildRowRouting.Merge([only]).Select(_marker).ToList()).IsEquivalentTo(["a", "b"]);
    await Assert.That(RebuildRowRouting.Merge([]).Count).IsEqualTo(0);
    await Assert.That(() => RebuildRowRouting.Merge(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Merge_MixedOriginsFallBackToCommitOrderAsync() {
    var first = new[] { _event("x2", commit: 2, version: 1, originSequence: 1) };
    var second = new[] { _event("x1", commit: 1, version: 1) };

    var merged = RebuildRowRouting.Merge([first, second]);

    await Assert.That(merged.Select(_marker).ToList()).IsEquivalentTo(["x1", "x2"]);
    await Assert.That(merged[0].Payload).IsTypeOf<Probe>();
    var enumerated = new List<string>();
    foreach (var envelope in (System.Collections.IEnumerable)merged) {
      enumerated.Add(_marker((MessageEnvelope<IEvent>)envelope));
    }
    await Assert.That(enumerated).IsEquivalentTo(["x1", "x2"]);
  }

  [Test]
  public async Task RunRekeyedRowAsync_Default_FoldsTheMergedSourcesAsync() {
    var runner = new RecordingRunner();
    var first = new[] { _event("a", commit: 1, version: 1) };
    var second = new[] { _event("b", commit: 2, version: 1) };
    var row = Guid.NewGuid();

    await ((IPerspectiveRunner)runner).RunRekeyedRowAsync(row, "P", [second, first]);

    await Assert.That(runner.Folded.Single().Row).IsEqualTo(row);
    await Assert.That(runner.Folded.Single().Events.Select(_marker).ToList()).IsEquivalentTo(["a", "b"]);
  }

  [Test]
  public async Task Ledger_CollectsEachSourceOnceAndHandsOverOnlyTouchedRowsAsync() {
    var ledger = new RebuildRekeyLedger();
    var row = Guid.NewGuid();
    var other = Guid.NewGuid();
    var source = Guid.NewGuid();

    await Assert.That(RebuildRekeyLedger.Current).IsNull();
    using (RebuildRekeyLedger.Begin(ledger)) {
      await Assert.That(RebuildRekeyLedger.Current).IsSameReferenceAs(ledger);
      var inner = new RebuildRekeyLedger();
      using (RebuildRekeyLedger.Begin(inner)) {
        await Assert.That(RebuildRekeyLedger.Current).IsSameReferenceAs(inner);
      }
      await Assert.That(RebuildRekeyLedger.Current).IsSameReferenceAs(ledger).Because("a scope restores the run it replaced");
    }
    await Assert.That(RebuildRekeyLedger.Current).IsNull();

    ledger.Add(new RebuildPartition(row, source, [_event("first read")]));
    ledger.Add(new RebuildPartition(row, source, [_event("second read")]));
    ledger.Add(new RebuildPartition(other, source, [_event("other")]));
    var touched = ledger.TakeTouched();

    await Assert.That(touched.Select(t => t.Row).ToList()).IsEquivalentTo([row, other]);
    await Assert.That(touched[0].Sources.Single().Select(_marker).ToList()).IsEquivalentTo(["second read"])
      .Because("a stream read twice in a run (a catch-up pass) contributes what it holds now, once");
    await Assert.That(ledger.TakeTouched()).IsEmpty().Because("nothing new was recorded since");

    ledger.Add(new RebuildPartition(row, Guid.NewGuid(), [_event("later source")]));
    var again = ledger.TakeTouched();
    await Assert.That(again.Single().Sources.Count).IsEqualTo(2)
      .Because("a row folded again is folded from every source the run has found, not only the new one");
    await Assert.That(() => RebuildRekeyLedger.Begin(null!)).Throws<ArgumentNullException>();
    await Assert.That(() => ledger.Add(null!)).Throws<ArgumentNullException>();
  }

  /// <summary>
  /// The rebuilder folds each re-keyed row once, after it has read every stream, from every source it found; a row
  /// whose fold fails is logged and the rest of the rebuild carries on, as a failed stream does.
  /// </summary>
  [Test]
  public async Task Rebuild_FoldsEachRekeyedRowOnceFromEverySourceAfterReadingTheStreamsAsync() {
    var target = Guid.NewGuid();
    var failing = Guid.NewGuid();
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    var runner = new LedgerRunner(new Dictionary<Guid, RebuildPartition[]> {
      [first] = [new RebuildPartition(target, first, [_event("a", commit: 1, version: 1)])],
      [second] = [new RebuildPartition(target, second, [_event("b", commit: 2, version: 1)]),
                  new RebuildPartition(failing, second, [_event("c", commit: 3, version: 2)])],
    }) { FailFor = failing };
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(new SingleRunnerRegistry(runner));
    await using var sp = services.BuildServiceProvider();
    var rebuilder = new PerspectiveRebuilder(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PerspectiveRebuilder>.Instance);

    var result = await rebuilder.RebuildStreamsAsync("P", [first, second]);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(runner.RekeyedFolds.Count).IsEqualTo(2);
    var sourceCount = runner.RekeyedFolds.Single(f => f.Row == target).SourceCount;
    await Assert.That(sourceCount).IsEqualTo(2).Because("both sources re-keyed onto the row are folded together, once");
    await Assert.That(runner.ReadBeforeFold).IsTrue().Because("the row is folded after every stream was read");
    await Assert.That(result.StreamsProcessed).IsEqualTo(1)
      .Because("the folded row counts as rebuilt; the failed one and the two sources that only carried re-keys do not");
  }

  // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

  private static string _marker(MessageEnvelope<IEvent> envelope) => ((Probe)envelope.Payload).Marker;

  private static MessageEnvelope<IEvent> _event(string marker, long? commit = null, long? version = null, long? originSequence = null) => new() {
    MessageId = MessageId.New(),
    Payload = new Probe(marker),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    LocalCommitSequence = commit,
    LocalStreamVersion = version,
    SourceServiceId = originSequence is null ? Guid.Empty : _origin,
    SourceCommitSequence = originSequence ?? 0,
  };

  private class RecordingRunner : IPerspectiveRunner {
    public List<(Guid Row, IReadOnlyList<MessageEnvelope<IEvent>> Events)> Folded { get; } = [];
    public Type PerspectiveType => typeof(object);

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(Completion(streamId, perspectiveName, Guid.Empty));

    public Task<PerspectiveCursorCompletion> RunWithEventsAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId,
        IReadOnlyList<MessageEnvelope<IEvent>> events, CancellationToken cancellationToken = default) {
      Folded.Add((streamId, events));
      return Task.FromResult(Completion(streamId, perspectiveName, events.Count == 0 ? Guid.Empty : events[^1].MessageId.Value));
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    protected static PerspectiveCursorCompletion Completion(Guid streamId, string perspectiveName, Guid last) => new() {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = last,
      RowChanged = last != Guid.Empty,
      Status = last == Guid.Empty ? PerspectiveProcessingStatus.None : PerspectiveProcessingStatus.Completed,
    };
  }

  /// <summary>Behaves as a generated runner does inside a run: hands each re-keyed partition to the open ledger.</summary>
  private sealed class LedgerRunner(Dictionary<Guid, RebuildPartition[]> partitions) : RecordingRunner, IPerspectiveRunner {
    private int _reads;
    public Guid FailFor { get; init; }
    public List<(Guid Row, int SourceCount)> RekeyedFolds { get; } = [];
    public bool ReadBeforeFold { get; private set; }

    public Task<IReadOnlyList<PerspectiveCursorCompletion>> RunRebuildAsync(Guid physicalStreamId, string perspectiveName, CancellationToken cancellationToken = default) {
      _reads++;
      foreach (var partition in partitions[physicalStreamId]) {
        RebuildRekeyLedger.Current!.Add(partition);
      }
      return Task.FromResult<IReadOnlyList<PerspectiveCursorCompletion>>([Completion(physicalStreamId, perspectiveName, Guid.Empty)]);
    }

    public Task<PerspectiveCursorCompletion> RunRekeyedRowAsync(Guid rowStreamId, string perspectiveName,
        IReadOnlyList<IReadOnlyList<MessageEnvelope<IEvent>>> rekeyedSources, CancellationToken cancellationToken = default) {
      ReadBeforeFold = _reads == partitions.Count;
      RekeyedFolds.Add((rowStreamId, rekeyedSources.Count));
      if (rowStreamId == FailFor) {
        throw new InvalidOperationException("the row's fold failed");
      }
      return Task.FromResult(Completion(rowStreamId, perspectiveName, rekeyedSources[^1][^1].MessageId.Value));
    }
  }

  private sealed class SingleRunnerRegistry(IPerspectiveRunner runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }
}
