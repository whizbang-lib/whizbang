// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Events.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Tests.Perspectives.RunnerBase;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// A rebuild reads its streams' events a batch at a time (#2513): one read per batch of
/// <see cref="PerspectiveRebuildOptions.StreamReadBatchSize"/> streams, never one per stream, and folds each stream
/// from that read exactly as it folds the stream it reads on its own. The round trips are counted at the event store,
/// and every result is compared with the per-stream rebuild of the same streams: rows, checkpoints, cursors, counts
/// and progress, over a multi-event stream whose ids run against its versions, a received stream whose arrival runs
/// against its origin's order, a stream with no events, one with only events the perspective does not fold, and a
/// batch boundary inside the set.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#batched-reads</docs>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs</code-under-test>
public class PerspectiveRebuilderBatchedReadTests {
  private static readonly Guid _multi = TrackedGuid.New().Value;
  private static readonly Guid _empty = TrackedGuid.New().Value;
  private static readonly Guid _received = TrackedGuid.New().Value;
  private static readonly Guid _ignoredOnly = TrackedGuid.New().Value;
  private static readonly Guid _mixed = TrackedGuid.New().Value;
  private static readonly Guid _origin = TrackedGuid.New().Value;
  private static readonly Guid[] _streams = [_multi, _empty, _received, _ignoredOnly, _mixed];

  [Test]
  public async Task Rebuild_ReadsEachBatchOfStreamsWithOneRead_NotOneReadPerStreamAsync() {
    var batched = _world(batchSize: 2);
    var perStream = _world(batchSize: 1);

    var batchedResult = await batched.Rebuilder.RebuildStreamsAsync(RbHarness.NAME, _streams);
    var perStreamResult = await perStream.Rebuilder.RebuildStreamsAsync(RbHarness.NAME, _streams);

    await Assert.That(batchedResult.Success).IsTrue().Because(batchedResult.Error ?? "");
    await Assert.That(perStreamResult.Success).IsTrue().Because(perStreamResult.Error ?? "");
    await Assert.That(perStream.Events.RoundTrips).IsEqualTo(5).Because("the control: one read per stream");
    await Assert.That(batched.Events.StreamReads).IsEmpty().Because("no stream of a batch is read on its own");
    await Assert.That(batched.Events.BatchReads.Count).IsEqualTo(3).Because("five streams in batches of two are three reads");
    await Assert.That(batched.Events.BatchReads.SelectMany(b => b)).IsEquivalentTo(_streams)
      .Because("every stream is read once, by the batch it falls in");
    await Assert.That(batched.Events.BatchReads.Select(b => b.Count)).IsEquivalentTo([2, 2, 1]);
  }

  [Test]
  public async Task Rebuild_FromBatchedReads_FoldsEveryStreamAsThePerStreamRebuildDoesAsync() {
    var batched = _world(batchSize: 2);
    var perStream = _world(batchSize: 1);

    var batchedResult = await batched.Rebuilder.RebuildStreamsAsync(RbHarness.NAME, _streams);
    var perStreamResult = await perStream.Rebuilder.RebuildStreamsAsync(RbHarness.NAME, _streams);

    // The expected folds, so the comparison below cannot pass by both paths being equally wrong.
    await Assert.That(batched.Rows.Row(_multi).Marks).IsEqualTo("abc").Because("stream order, not the ids that run against it");
    await Assert.That(batched.Rows.Row(_received).Marks).IsEqualTo("12").Because("the origin's order, not arrival order");
    await Assert.That(batched.Rows.Row(_mixed).Marks).IsEqualTo("xo");
    await Assert.That(batched.Rows.Has(_empty)).IsFalse();
    await Assert.That(batched.Rows.Has(_ignoredOnly)).IsFalse();

    foreach (var stream in _streams) {
      await Assert.That(batched.Rows.Has(stream)).IsEqualTo(perStream.Rows.Has(stream));
      if (perStream.Rows.Has(stream)) {
        await Assert.That(batched.Rows.Row(stream)).IsEqualTo(perStream.Rows.Row(stream));
        await Assert.That(batched.Rows.MetadataOf(stream).EventId).IsEqualTo(perStream.Rows.MetadataOf(stream).EventId)
          .Because("the row is stamped with the same last event");
      }
    }
    await Assert.That(_cursors(batched)).IsEquivalentTo(_cursors(perStream));
    await Assert.That(_counts(batchedResult)).IsEqualTo(_counts(perStreamResult));
    await Assert.That(batchedResult.StreamsProcessed).IsEqualTo(3);
    await Assert.That(batchedResult.EventsReplayed).IsEqualTo(7);
    await Assert.That(_progress(batched)).IsEquivalentTo(_progress(perStream));
  }

  [Test]
  public async Task Rebuild_WhenTheStoreReadsNoStreamsInBatches_ReadsEachStreamOnItsOwnAsync() {
    var world = _world(batchSize: 2, readsInBatches: false);

    var result = await world.Rebuilder.RebuildStreamsAsync(RbHarness.NAME, _streams);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(world.Events.StreamReads).IsEquivalentTo(_streams);
    await Assert.That(world.Rows.Row(_multi).Marks).IsEqualTo("abc");
  }

  [Test]
  public async Task Rebuild_WhenABatchedReadFails_LogsIt_AndReadsThatBatchsStreamsOnTheirOwnAsync() {
    var world = _world(batchSize: 2);
    world.Events.BatchReadFails = new InvalidOperationException("batch refused");

    var result = await world.Rebuilder.RebuildStreamsAsync(RbHarness.NAME, _streams);

    await Assert.That(result.Success).IsTrue().Because("a failed batched read costs round trips, not streams");
    await Assert.That(world.Events.StreamReads).IsEquivalentTo(_streams);
    await Assert.That(world.Rows.Row(_received).Marks).IsEqualTo("12");
    await Assert.That(world.Log.Lines.Count(l => l.Level == LogLevel.Warning && l.Message.Contains("one read of"))).IsEqualTo(3)
      .Because("each failed batched read is logged, once");
  }

  [Test]
  public async Task Rebuild_WhenTheBatchedReadLeavesAStreamOut_ReadsThatStreamOnItsOwnAsync() {
    var world = _world(batchSize: 10);
    world.Events.BatchOmits.Add(_mixed);

    var result = await world.Rebuilder.RebuildStreamsAsync(RbHarness.NAME, _streams);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(world.Events.BatchReads.Count).IsEqualTo(1);
    await Assert.That(world.Events.StreamReads).IsEquivalentTo([_mixed]);
    await Assert.That(world.Rows.Row(_mixed).Marks).IsEqualTo("xo");
  }

  [Test]
  public async Task Rebuild_LeavesAStreamWithArchivedData_ToItsOwnRead_ThroughTheArchiveAsync() {
    var world = _world(batchSize: 10, extents: new() { [_multi] = ArchivedStreamExtent.Prefix, [_mixed] = ArchivedStreamExtent.Whole });

    var result = await world.Rebuilder.RebuildStreamsAsync(
      RbHarness.NAME, _streams, new RebuildOrigin { ArchivedData = RebuildArchivedData.IncludeAll });

    await Assert.That(result.Success).IsTrue();
    await Assert.That(world.Events.BatchReads.Single()).IsEquivalentTo([_empty, _received, _ignoredOnly])
      .Because("a stream with archived data reads through the archive, which a batched read of the hot store cannot");
    await Assert.That(world.Events.StreamReads).IsEquivalentTo([_multi, _mixed]);
    await Assert.That(world.Rows.Row(_multi).Marks).IsEqualTo("abc");
  }

  [Test]
  public async Task Rebuild_OfStreamsThatAllHaveArchivedData_MakesNoBatchedReadAsync() {
    var world = _world(batchSize: 10, extents: new() { [_multi] = ArchivedStreamExtent.Prefix });

    var result = await world.Rebuilder.RebuildStreamsAsync(
      RbHarness.NAME, [_multi], new RebuildOrigin { ArchivedData = RebuildArchivedData.IncludeWhileLive });

    await Assert.That(result.Success).IsTrue();
    await Assert.That(world.Events.BatchReads).IsEmpty();
    await Assert.That(world.Events.StreamReads).IsEquivalentTo([_multi]);
  }

  [Test]
  public async Task Rebuild_CanceledDuringABatchedRead_FoldsNoStreamOfThatBatchOrAfterAsync() {
    var world = _world(batchSize: 2);
    using var cancel = new CancellationTokenSource();
    world.Events.BeforeBatchRead = ids => {
      if (ids.Contains(_received)) {
        cancel.Cancel();
      }
    };

    var result = await world.Rebuilder.RebuildStreamsAsync(RbHarness.NAME, _streams, cancel.Token);

    await Assert.That(result.Success).IsFalse().Because("a canceled rebuild ends failed, as it did reading each stream");
    await Assert.That(world.Rows.Has(_multi)).IsTrue().Because("the batch before the cancellation was folded");
    await Assert.That(world.Rows.Has(_received)).IsFalse();
    await Assert.That(world.Rows.Has(_mixed)).IsFalse();
    await Assert.That(world.Events.BatchReads.Count).IsEqualTo(2).Because("no batch is read after the cancellation");
  }

  [Test]
  [Arguments(0)]
  [Arguments(-5)]
  public async Task Rebuild_WithABatchSizeBelowOne_ReadsEachStreamOnItsOwnAsync(int batchSize) {
    var world = _world(batchSize);

    var result = await world.Rebuilder.RebuildStreamsAsync(RbHarness.NAME, _streams);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(world.Events.BatchReads).IsEmpty();
    await Assert.That(world.Events.StreamReads).IsEquivalentTo(_streams);
  }

  [Test]
  public async Task Rebuild_WithNoRebuildOptionsRegistered_ReadsAHundredStreamsPerBatchAsync() {
    var world = _world(batchSize: null);

    var result = await world.Rebuilder.RebuildStreamsAsync(RbHarness.NAME, _streams);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(world.Events.BatchReads.Single()).IsEquivalentTo(_streams);
  }

  [Test]
  public async Task PerspectiveRebuildOptions_ReadsAHundredStreamsPerBatch_ByDefaultAsync() {
    await Assert.That(new PerspectiveRebuildOptions().StreamReadBatchSize).IsEqualTo(100);
  }

  // ===== Fixtures =====

  private static List<(Guid StreamId, Guid LastEventId, int EventsProcessed)> _cursors(World world) =>
    [.. world.Completer.Completed.Select(c => (c.StreamId, c.LastEventId, c.EventsProcessed))];

  private static (int, int, int, int, int) _counts(RebuildResult result) =>
    (result.StreamsProcessed, result.EventsReplayed, result.StreamsWithoutEvents, result.StreamsSkipped, result.StreamsRequested);

  private static List<(int, int, int)> _progress(World world) =>
    [.. world.Dispatcher.Published.OfType<PerspectiveRebuildProgress>().Select(p => (p.ProcessedStreams, p.TotalStreams, p.EventsReplayed))];

  /// <summary>
  /// The same streams in every world: the stream and event ids are fixed, so two worlds' rows and cursors compare; the store
  /// and the rows are each world's own.
  /// </summary>
  private static World _world(int? batchSize, bool readsInBatches = true, Dictionary<Guid, ArchivedStreamExtent>? extents = null) {
    var harness = new RbHarness();
    var events = harness.Events;
    events.ReadsInBatches = readsInBatches;
    // Versions a, b, c; the ids run the other way, so an id-ordered fold would give "cba".
    events.Add(_multi, new RbMarked { StreamId = _multi, Mark = "a" }, id: new Guid("00000000-0000-7000-8000-000000000003"));
    events.Add(_multi, new RbMarked { StreamId = _multi, Mark = "b" }, id: new Guid("00000000-0000-7000-8000-000000000002"));
    events.Add(_multi, new RbMarked { StreamId = _multi, Mark = "c" }, id: new Guid("00000000-0000-7000-8000-000000000001"));
    // Received: the origin's second event arrived first.
    events.Store(_received, _receivedEvent("2", 2, new Guid("00000000-0000-7000-8000-000000000012")));
    events.Store(_received, _receivedEvent("1", 1, new Guid("00000000-0000-7000-8000-000000000011")));
    events.Add(_ignoredOnly, new RbIgnored { StreamId = _ignoredOnly }, id: new Guid("00000000-0000-7000-8000-000000000021"));
    events.Add(_mixed, new RbMarked { StreamId = _mixed, Mark = "x" }, id: new Guid("00000000-0000-7000-8000-000000000031"));
    events.Add(_mixed, new RbIgnored { StreamId = _mixed }, id: new Guid("00000000-0000-7000-8000-000000000032"));
    events.Add(_mixed, new RbOther { StreamId = _mixed }, id: new Guid("00000000-0000-7000-8000-000000000033"));

    var log = new RbLog();
    var completer = new RecordingCompleter();
    var dispatcher = new RebuildEventDispatcherDouble();
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(new Registry(harness.Runner));
    services.AddSingleton<IPerspectiveCheckpointCompleter>(completer);
    services.AddSingleton<IDispatcher>(dispatcher);
    if (batchSize is { } size) {
      services.AddSingleton(Options.Create(new PerspectiveRebuildOptions { StreamReadBatchSize = size }));
    }
    if (extents is not null) {
      services.AddSingleton<IArchivedStreamExtents>(new Extents(extents));
    }
    var sp = services.BuildServiceProvider();
    var rebuilder = new PerspectiveRebuilder(sp.GetRequiredService<IServiceScopeFactory>(), new TypedLog(log));
    return new World(rebuilder, events, harness.Rows, completer, dispatcher, log);
  }

  private static MessageEnvelope<IEvent> _receivedEvent(string mark, long originSequence, Guid id) => new() {
    MessageId = MessageId.From(id),
    Payload = new RbMarked { StreamId = _received, Mark = mark },
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    SourceServiceId = _origin,
    SourceCommitSequence = originSequence,
  };

  private sealed record World(
    PerspectiveRebuilder Rebuilder, RbEvents Events, RbRows Rows, RecordingCompleter Completer,
    RebuildEventDispatcherDouble Dispatcher, RbLog Log);

  private sealed class RecordingCompleter : IPerspectiveCheckpointCompleter {
    public List<PerspectiveCursorCompletion> Completed { get; } = [];

    public Task CompleteAsync(IReadOnlyList<PerspectiveCursorCompletion> completions, CancellationToken cancellationToken = default) {
      Completed.AddRange(completions);
      return Task.CompletedTask;
    }
  }

  private sealed class TypedLog(RbLog inner) : ILogger<PerspectiveRebuilder> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => inner.Log(logLevel, eventId, state, exception, formatter);
  }

  private sealed class Extents(Dictionary<Guid, ArchivedStreamExtent> extents) : IArchivedStreamExtents {
    public Task<IReadOnlyDictionary<Guid, ArchivedStreamExtent>> GetExtentsAsync(
        IReadOnlyCollection<Guid> streamIds, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyDictionary<Guid, ArchivedStreamExtent>>(
        streamIds.Where(extents.ContainsKey).ToDictionary(id => id, id => extents[id]));
  }

  private sealed class Registry(IPerspectiveRunner runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }
}
