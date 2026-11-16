// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.Tests.Observability;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2366: a rewind asked for again with the same trigger, on a stream with nothing stored since the last one, replays the
/// same input to the same state. It is settled with the last rewind's completion and never run again: its rows complete,
/// a repeat is counted, and one warning names the perspective and the stream once the repeats reach the threshold.
/// </summary>
/// <remarks>
/// The coordinator here keeps the cursor flagged with the same trigger on every batch, which is the shape of the loop: a
/// stream something keeps flagging. The guard holds whatever does the flagging.
/// </remarks>
public partial class PerspectiveWorkerDeepPathChannelTests {
  private const int REWIND_REPEATING_EVENT_ID = 83;

  [Test]
  public async Task Rewind_AskedAgainOnAnUnchangedStream_IsSettledWithoutReplaying_CountedAndWarnedOnceAsync() {
    var (streamId, perspectiveName) = (Guid.CreateVersion7(), "Deep.RewindRepeatSettledPerspective");
    var runner = new RecordingRunner();
    var store = new SequencedEventStore { LastSequence = 4 };
    var logger = new FakeLogger<PerspectiveWorker>();
    using var meterFactory = new TestMeterFactory();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(meterFactory));
    using var meters = new MetricAssertionHelper(meterFactory.CreatedMeters[0]);
    var (worker, harness, _) = _buildBranchWorker(_rewindCoordinator(streamId, perspectiveName, Guid.CreateVersion7()),
      new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]), new BranchSetup {
        EventStore = store,
        Logger = logger,
        Metrics = metrics,
        RewindOptions = new PerspectiveRewindOptions { RepeatWarningThreshold = 2 },
      });

    var settled = new List<PerspectiveWork>();
    for (var lap = 0; lap < 4; lap++) {
      var work = _branchWork(streamId, perspectiveName, PerspectiveProcessingStatus.RewindRequired);
      settled.Add(work);
      await worker.ProcessChannelBatchAsync([work], CancellationToken.None);
    }
    await worker.ProcessChannelBatchAsync([], CancellationToken.None);

    await Assert.That(runner.RunCallCount).IsEqualTo(1)
      .Because("the first rewind replays the stream; the same trigger on the same stream again could change nothing");
    await Assert.That(harness.CompletionCapture.EventWorkIds).Contains(settled[^1].WorkId)
      .Because("a settled repeat completes its rows exactly as the rewind did");
    var repeats = meters.GetByName("whizbang.perspective.rewind_repeats")
      .Where(m => m.Tags.GetValueOrDefault("perspective_name") == perspectiveName).Sum(m => m.Value);
    await Assert.That(repeats).IsEqualTo(3d);
    var warnings = logger.Collector.GetSnapshot().Where(r => r.Id.Id == REWIND_REPEATING_EVENT_ID).ToList();
    await Assert.That(warnings).Count().IsEqualTo(1).Because("one warning, when the repeats reach the threshold, not one per lap");
    await Assert.That(warnings[0].Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(warnings[0].Message).Contains(streamId.ToString(), StringComparison.Ordinal);
    await Assert.That(warnings[0].Message).Contains(perspectiveName, StringComparison.Ordinal);
  }

  [Test]
  public async Task Rewind_AskedAgainAfterAnEventWasStored_ReplaysAgainAsync() {
    var (streamId, perspectiveName) = (Guid.CreateVersion7(), "Deep.RewindRepeatNewEventPerspective");
    var runner = new RecordingRunner();
    var store = new SequencedEventStore { LastSequence = 4 };
    var (worker, _, _) = _buildBranchWorker(_rewindCoordinator(streamId, perspectiveName, Guid.CreateVersion7()),
      new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]), new BranchSetup { EventStore = store });

    await worker.ProcessChannelBatchAsync(
      [_branchWork(streamId, perspectiveName, PerspectiveProcessingStatus.RewindRequired)], CancellationToken.None);
    store.LastSequence = 5;
    await worker.ProcessChannelBatchAsync(
      [_branchWork(streamId, perspectiveName, PerspectiveProcessingStatus.RewindRequired)], CancellationToken.None);

    await Assert.That(runner.RunCallCount).IsEqualTo(2).Because("a stream that changed is new input, and is replayed");
  }

  [Test]
  public async Task Rewind_OnAStreamWhoseVersionIsUnknown_AlwaysReplaysAsync() {
    var (streamId, perspectiveName) = (Guid.CreateVersion7(), "Deep.RewindRepeatUnknownVersionPerspective");
    var runner = new RecordingRunner();
    var (worker, _, _) = _buildBranchWorker(_rewindCoordinator(streamId, perspectiveName, Guid.CreateVersion7()),
      new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]),
      new BranchSetup { EventStore = new SequencedEventStore() });

    for (var lap = 0; lap < 2; lap++) {
      await worker.ProcessChannelBatchAsync(
        [_branchWork(streamId, perspectiveName, PerspectiveProcessingStatus.RewindRequired)], CancellationToken.None);
    }

    await Assert.That(runner.RunCallCount).IsEqualTo(2)
      .Because("without the stream's last version nothing proves the input unchanged, so the rewind runs");
  }

  [Test]
  public async Task Rewind_ThatDidNotComplete_IsNotRememberedAsAnOutcomeAsync() {
    var (streamId, perspectiveName) = (Guid.CreateVersion7(), "Deep.RewindRepeatIncompletePerspective");
    var runner = new RecordingRunner { RewindDropsTheTrigger = true };
    var (worker, _, _) = _buildBranchWorker(_rewindCoordinator(streamId, perspectiveName, Guid.CreateVersion7()),
      new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]),
      new BranchSetup { EventStore = new SequencedEventStore { LastSequence = 4 } });

    for (var lap = 0; lap < 2; lap++) {
      await worker.ProcessChannelBatchAsync(
        [_branchWork(streamId, perspectiveName, PerspectiveProcessingStatus.RewindRequired)], CancellationToken.None);
    }

    await Assert.That(runner.RewindCallCount).IsEqualTo(2)
      .Because("only a completed rewind is an outcome a repeat may stand on");
  }
}
