// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The set-based apply of one batch (#2246): every pair's apply lock taken as a set before the first prepare and released
/// after the last completion, the prepared rows written once per DbContext through that context's sink and the flush
/// policy, each outcome routed to its own pair, and a pair's failure kept to that pair. Component: the batch prepares and
/// completes its pairs in parallel.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#the-batch</docs>
public class PerspectiveApplyBatchTests {
  private const string PERSPECTIVE = "Tests.Orders";
  private static readonly object _contextA = "context-a";
  private static readonly object _contextB = "context-b";

  [Test]
  public async Task TheLocks_AreTakenInOrderBeforeTheFirstPrepare_AndReleasedAfterTheLastCompletionAsync() {
    var log = new ConcurrentQueue<string>();
    var coordinator = new RecordingCoordinator(log);
    var streams = new[] { Guid.Parse("00000000-0000-0000-0000-000000000003"), Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002") };
    var pairs = streams.Select(s => _pair(s, new FakeRunner(log) { WriteKey = _contextA })).ToList();

    await PerspectiveApplyBatch.RunAsync(pairs, _services(coordinator, sinks: _sinks((_contextA, new RecordingSink(log)))), CancellationToken.None);

    var entries = log.ToArray();
    var acquires = entries.Where(e => e.StartsWith("acquire", StringComparison.Ordinal)).ToList();
    await Assert.That(acquires).IsEquivalentTo(
      streams.Order().Select(s => $"acquire {s}").ToList(), TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("the locks are taken in one fixed order, so two batches cannot deadlock");
    var lastAcquire = Array.FindLastIndex(entries, e => e.StartsWith("acquire", StringComparison.Ordinal));
    var firstPrepare = Array.FindIndex(entries, e => e.StartsWith("prepare", StringComparison.Ordinal));
    var lastComplete = Array.FindLastIndex(entries, e => e.StartsWith("complete", StringComparison.Ordinal));
    var firstRelease = Array.FindIndex(entries, e => e.StartsWith("release", StringComparison.Ordinal));
    await Assert.That(lastAcquire).IsLessThan(firstPrepare);
    await Assert.That(lastComplete).IsLessThan(firstRelease)
      .Because("each pair's lock is held across the batch write until its completion");
    await Assert.That(entries.Count(e => e.StartsWith("release", StringComparison.Ordinal))).IsEqualTo(3);
  }

  [Test]
  public async Task EachContextsWrites_GoThroughItsOwnSink_AndEveryOutcomeReachesItsPairAsync() {
    var log = new ConcurrentQueue<string>();
    var sinkA = new RecordingSink(log) { Answer = w => PerspectiveWriteOutcome.Written };
    var sinkB = new RecordingSink(log) { Answer = w => PerspectiveWriteOutcome.Refused };
    var sinkDefault = new RecordingSink(log) { Answer = w => PerspectiveWriteOutcome.Conflict(PerspectiveRowVersion.Absent) };
    var a1 = _pair(Guid.NewGuid(), new FakeRunner(log) { WriteKey = _contextA });
    var b1 = _pair(Guid.NewGuid(), new FakeRunner(log) { WriteKey = _contextB });
    var a2 = _pair(Guid.NewGuid(), new FakeRunner(log) { WriteKey = _contextA });
    var unkeyed = _pair(Guid.NewGuid(), new FakeRunner(log) { WriteKey = null });

    await PerspectiveApplyBatch.RunAsync([a1, b1, a2, unkeyed],
      _services(null, sinks: _sinks((_contextA, sinkA), (_contextB, sinkB)), defaultSink: sinkDefault), CancellationToken.None);

    await Assert.That(sinkA.Calls).IsEqualTo(1).Because("one write per context, whatever the batch's size");
    await Assert.That(sinkA.Written.Select(w => w.StreamId)).IsEquivalentTo(new[] { a1.StreamId, a2.StreamId });
    await Assert.That(sinkB.Written.Select(w => w.StreamId)).IsEquivalentTo(new[] { b1.StreamId });
    await Assert.That(sinkDefault.Written.Select(w => w.StreamId)).IsEquivalentTo(new[] { unkeyed.StreamId })
      .Because("a write that names no context goes to the sink registered without a key");
    await Assert.That(_runner(a1).CompletedWith!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Written);
    await Assert.That(_runner(a2).CompletedWith!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Written);
    await Assert.That(_runner(b1).CompletedWith!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Refused);
    await Assert.That(_runner(unkeyed).CompletedWith!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Conflict);
    foreach (var pair in new[] { a1, b1, a2, unkeyed }) {
      await Assert.That(pair.Result).IsSameReferenceAs(_runner(pair).Completion);
      await Assert.That(pair.Failure).IsNull();
    }
  }

  [Test]
  public async Task APairThatRunsPerPair_IsCompletedWithNoOutcome_AndWritesNothingAsync() {
    var log = new ConcurrentQueue<string>();
    var sink = new RecordingSink(log);
    var perPair = _pair(Guid.NewGuid(), new FakeRunner(log) { RunsPerPair = true });
    var written = _pair(Guid.NewGuid(), new FakeRunner(log) { WriteKey = _contextA });

    await PerspectiveApplyBatch.RunAsync([perPair, written], _services(null, sinks: _sinks((_contextA, sink))), CancellationToken.None);

    await Assert.That(_runner(perPair).Completed).IsTrue();
    await Assert.That(_runner(perPair).CompletedWith).IsNull();
    await Assert.That(sink.Written.Select(w => w.StreamId)).IsEquivalentTo(new[] { written.StreamId });
  }

  [Test]
  public async Task AFailedPrepare_StaysWithItsPair_AndTheOthersAreWrittenAndCompletedAsync() {
    var log = new ConcurrentQueue<string>();
    var sink = new RecordingSink(log);
    var broken = _pair(Guid.NewGuid(), new FakeRunner(log) { PrepareFails = true, WriteKey = _contextA });
    var healthy = _pair(Guid.NewGuid(), new FakeRunner(log) { WriteKey = _contextA });

    await PerspectiveApplyBatch.RunAsync([broken, healthy], _services(null, sinks: _sinks((_contextA, sink))), CancellationToken.None);

    await Assert.That(broken.Failure).IsTypeOf<InvalidOperationException>();
    await Assert.That(broken.Result).IsNull();
    await Assert.That(_runner(broken).Completed).IsFalse().Because("a pair whose prepare threw has nothing to complete");
    await Assert.That(sink.Written.Select(w => w.StreamId)).IsEquivalentTo(new[] { healthy.StreamId });
    await Assert.That(healthy.Result).IsNotNull();
  }

  [Test]
  public async Task AFailedCompletion_StaysWithItsPairAsync() {
    var log = new ConcurrentQueue<string>();
    var broken = _pair(Guid.NewGuid(), new FakeRunner(log) { CompleteFails = true, WriteKey = _contextA });
    var healthy = _pair(Guid.NewGuid(), new FakeRunner(log) { WriteKey = _contextA });

    await PerspectiveApplyBatch.RunAsync([broken, healthy], _services(null, sinks: _sinks((_contextA, new RecordingSink(log)))), CancellationToken.None);

    await Assert.That(broken.Failure).IsTypeOf<InvalidOperationException>();
    await Assert.That(healthy.Result).IsNotNull();
    await Assert.That(healthy.Failure).IsNull();
  }

  [Test]
  public async Task ASinkThatFailsAsAWhole_FailsEachOfItsWrites_SoEachIsAppliedAgainOnItsOwnAsync() {
    var log = new ConcurrentQueue<string>();
    var failing = new RecordingSink(log) { Throws = true };
    var a = _pair(Guid.NewGuid(), new FakeRunner(log) { WriteKey = _contextA });
    var b = _pair(Guid.NewGuid(), new FakeRunner(log) { WriteKey = _contextB });

    await PerspectiveApplyBatch.RunAsync([a, b],
      _services(null, sinks: _sinks((_contextA, failing), (_contextB, new RecordingSink(log)))), CancellationToken.None);

    await Assert.That(_runner(a).CompletedWith!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Failed);
    await Assert.That(_runner(a).CompletedWith!.Value.Error!.Message).IsEqualTo("the connection was lost");
    await Assert.That(_runner(b).CompletedWith!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Written)
      .Because("another context's writes are not part of the failure");
  }

  [Test]
  public async Task AContextWithNoSink_FailsItsWrites_SoTheyAreWrittenPerStreamAsync() {
    var log = new ConcurrentQueue<string>();
    var keyless = _pair(Guid.NewGuid(), new FakeRunner(log) { WriteKey = _contextB });
    var unkeyed = _pair(Guid.NewGuid(), new FakeRunner(log) { WriteKey = null });

    await PerspectiveApplyBatch.RunAsync([keyless, unkeyed], _services(null, sinks: _sinks()), CancellationToken.None);

    await Assert.That(_runner(keyless).CompletedWith!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Failed);
    await Assert.That(_runner(keyless).CompletedWith!.Value.Error!.Message).Contains("context-b");
    await Assert.That(_runner(unkeyed).CompletedWith!.Value.Error!.Message).Contains("the default write sink");
  }

  [Test]
  public async Task AFlushPolicyThatAnswersTheWrongNumberOfOutcomes_FailsTheWritesAsync() {
    var log = new ConcurrentQueue<string>();
    var pair = _pair(Guid.NewGuid(), new FakeRunner(log) { WriteKey = _contextA });

    await PerspectiveApplyBatch.RunAsync([pair],
      _services(null, sinks: _sinks((_contextA, new RecordingSink(log))), policy: new ShortPolicy()), CancellationToken.None);

    await Assert.That(_runner(pair).CompletedWith!.Value.Status).IsEqualTo(PerspectiveWriteStatus.Failed);
    await Assert.That(_runner(pair).CompletedWith!.Value.Error!.Message).Contains("0 outcome(s) for 1 write(s)");
  }

  [Test]
  public async Task TheStateSource_PlansTheBatch_AndEveryPrepareReadsFromItAsync() {
    var log = new ConcurrentQueue<string>();
    var source = new RecordingStateSource();
    var first = _envelope(isFirst: true);
    var newStream = new PerspectiveApplyBatch.Pair(Guid.NewGuid(), PERSPECTIVE, new FakeRunner(log) { RunsPerPair = true }, null, [first]);
    var oldStream = new PerspectiveApplyBatch.Pair(Guid.NewGuid(), PERSPECTIVE, new FakeRunner(log) { RunsPerPair = true }, Guid.NewGuid(), [_envelope(isFirst: false)]);

    await PerspectiveApplyBatch.RunAsync([newStream, oldStream], _services(null, sinks: _sinks(), stateSource: source), CancellationToken.None);

    var planned = source.Planned!.Single(r => r.StreamId == newStream.StreamId);
    await Assert.That(planned with { EventIds = null })
      .IsEqualTo(new PerspectiveStateRequest(newStream.StreamId, PERSPECTIVE, first.MessageId.Value, KnownNew: true));
    await Assert.That(planned.EventIds).IsEquivalentTo(new[] { first.MessageId.Value })
      .Because("the batch plans the commit-sequence read of every event the pair folds");
    await Assert.That(source.Planned!.Single(r => r.StreamId == oldStream.StreamId).KnownNew).IsFalse();
    await Assert.That(_runner(newStream).BatchState).IsSameReferenceAs(source.Batch);
    await Assert.That(_runner(oldStream).BatchState).IsSameReferenceAs(source.Batch);
  }

  [Test]
  public async Task AnEmptyBatch_TakesNoLockAsync() {
    var log = new ConcurrentQueue<string>();

    await PerspectiveApplyBatch.RunAsync([], _services(new RecordingCoordinator(log), sinks: _sinks()), CancellationToken.None);

    await Assert.That(log).IsEmpty();
  }

  [Test]
  public async Task ACancellation_IsThrown_NotRecordedOnAPairAsync() {
    var log = new ConcurrentQueue<string>();
    using var cts = new CancellationTokenSource();
    var pair = _pair(Guid.NewGuid(), new FakeRunner(log) { OnPrepare = cts.Cancel, WriteKey = _contextA });

    await Assert.That(async () => await PerspectiveApplyBatch.RunAsync(
      [pair], _services(null, sinks: _sinks((_contextA, new RecordingSink(log)))), cts.Token)).Throws<OperationCanceledException>();
    await Assert.That(pair.Failure).IsNull();
  }

  // ---------------------------------------------------------------------------------------------------------------

  private static FakeRunner _runner(PerspectiveApplyBatch.Pair pair) => (FakeRunner)pair.Runner;

  private static PerspectiveApplyBatch.Pair _pair(Guid streamId, FakeRunner runner) =>
    new(streamId, PERSPECTIVE, runner, Guid.NewGuid(), [_envelope(isFirst: false)]);

  private static MessageEnvelope<IEvent> _envelope(bool isFirst) => new() {
    MessageId = MessageId.New(),
    Payload = new SampleEvent(),
    Hops = [],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    IsFirstInLocalStream = isFirst,
  };

  private sealed record SampleEvent : IEvent;

  private static ServiceCollection _sinks(params (object Key, IPerspectiveWriteSink Sink)[] sinks) {
    var services = new ServiceCollection();
    foreach (var (key, sink) in sinks) {
      services.AddKeyedSingleton(key, sink);
    }
    return services;
  }

  private static PerspectiveApplyBatch.Services _services(
      IPerspectiveApplyCoordinator? coordinator, ServiceCollection sinks, IPerspectiveWriteSink? defaultSink = null,
      IApplyBatchStrategy? policy = null, IPerspectiveStateSource? stateSource = null) {
    if (defaultSink is not null) {
      sinks.AddSingleton(defaultSink);
    }
    var provider = sinks.BuildServiceProvider();
    return new PerspectiveApplyBatch.Services(
      provider.GetRequiredService<IServiceScopeFactory>(), coordinator, stateSource, policy ?? new FlushEveryBatchStrategy(),
      Width: 4, NullLogger.Instance);
  }

  private sealed class Prepared(Guid streamId, PendingPerspectiveWrite? write, PerspectivePerPairReason reason)
    : PreparedPerspectiveApply(streamId, PERSPECTIVE, null, [], write, reason);

  private sealed class FakeRunner(ConcurrentQueue<string> log) : IPerspectiveRunner {
    public object? WriteKey { get; init; }
    public bool RunsPerPair { get; init; }
    public bool PrepareFails { get; init; }
    public bool CompleteFails { get; init; }
    public Action? OnPrepare { get; init; }
    public IPerspectiveBatchState? BatchState { get; private set; }
    public bool Completed { get; private set; }
    public PerspectiveWriteOutcome? CompletedWith { get; private set; }
    public PerspectiveCursorCompletion Completion { get; } = new() {
      StreamId = Guid.NewGuid(),
      PerspectiveName = PERSPECTIVE,
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Completed,
    };

    public Type PerspectiveType => typeof(FakeRunner);

    public Task<PreparedPerspectiveApply> PrepareAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, IReadOnlyList<MessageEnvelope<IEvent>> events,
        IPerspectiveBatchState? batchState, CancellationToken cancellationToken = default) {
      log.Enqueue($"prepare {streamId}");
      BatchState = batchState;
      OnPrepare?.Invoke();
      cancellationToken.ThrowIfCancellationRequested();
      if (PrepareFails) {
        throw new InvalidOperationException("the fold threw");
      }
      if (RunsPerPair) {
        return Task.FromResult<PreparedPerspectiveApply>(new Prepared(streamId, null, PerspectivePerPairReason.RewindBelowPosition));
      }
      var write = PendingPerspectiveWrite.Purge("wh_per_orders", streamId);
      return Task.FromResult<PreparedPerspectiveApply>(new Prepared(
        streamId, WriteKey is null ? write : write.ForSink(WriteKey), PerspectivePerPairReason.None));
    }

    public Task<PerspectiveCursorCompletion> CompleteAsync(
        PreparedPerspectiveApply prepared, PerspectiveWriteOutcome? outcome, CancellationToken cancellationToken = default) {
      log.Enqueue($"complete {prepared.StreamId}");
      Completed = true;
      CompletedWith = outcome;
      return CompleteFails ? throw new InvalidOperationException("the snapshot threw") : Task.FromResult(Completion);
    }

    public Task<PerspectiveCursorCompletion> RunAsync(Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
  }

  private sealed class RecordingSink(ConcurrentQueue<string> log) : IPerspectiveWriteSink {
    private readonly ConcurrentQueue<PendingPerspectiveWrite> _written = new();
    private int _calls;
    public Func<PendingPerspectiveWrite, PerspectiveWriteOutcome> Answer { get; init; } = _ => PerspectiveWriteOutcome.Written;
    public bool Throws { get; init; }
    public int Calls => _calls;
    public IReadOnlyList<PendingPerspectiveWrite> Written => [.. _written];

    public Task<IReadOnlyList<PerspectiveWriteOutcome>> WriteAsync(
        IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _calls);
      log.Enqueue($"write {writes.Count}");
      if (Throws) {
        throw new InvalidOperationException("the connection was lost");
      }
      foreach (var write in writes) {
        _written.Enqueue(write);
      }
      return Task.FromResult<IReadOnlyList<PerspectiveWriteOutcome>>([.. writes.Select(Answer)]);
    }
  }

  private sealed class ShortPolicy : IApplyBatchStrategy {
    public Task<IReadOnlyList<PerspectiveWriteOutcome>> FlushAsync(
        IPerspectiveWriteSink sink, IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyList<PerspectiveWriteOutcome>>([]);
  }

  private sealed class RecordingCoordinator(ConcurrentQueue<string> log) : IPerspectiveApplyCoordinator {
    public Task<IAsyncDisposable> AcquireAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      log.Enqueue($"acquire {streamId}");
      return Task.FromResult<IAsyncDisposable>(new Release(log, streamId));
    }

    private sealed class Release(ConcurrentQueue<string> log, Guid streamId) : IAsyncDisposable {
      public ValueTask DisposeAsync() {
        log.Enqueue($"release {streamId}");
        return ValueTask.CompletedTask;
      }
    }
  }

  private sealed class RecordingStateSource : IPerspectiveStateSource {
    public IReadOnlyList<PerspectiveStateRequest>? Planned { get; private set; }
    public IPerspectiveBatchState Batch { get; } = new BatchedPerspectiveStateSource().BeginBatch([]);

    public IPerspectiveBatchState BeginBatch(IReadOnlyList<PerspectiveStateRequest> pairs) {
      Planned = pairs;
      return Batch;
    }
  }
}
