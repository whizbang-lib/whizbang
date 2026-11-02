// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A rewind that fails on the claimed per-event path fails its work rows, so the rows are retried with backoff and
/// dead-lettered at <see cref="PerspectiveWorkerOptions.MaxPerspectiveEventAttempts"/> (#1983).
/// </summary>
/// <remarks>
/// Before, the failure was logged at Error and the group carried on as though the rewind had applied nothing: its rows
/// were completed (deleted) with nothing applied, no failure was recorded against them, and the cursor kept its
/// rewind flag, so the next work on the stream tried the same rewind again, failed again, and discarded its rows too.
/// Nothing ever bounded it. The drain path already parked the rows of a rewind that threw; the per-event path now does
/// the same, and its attempt line is a Warning because the dead-letter record is what reports the end.
/// </remarks>
public partial class PerspectiveWorkerDeepPathChannelTests {
  private const int REWIND_FAILED_EVENT_ID = 58;

  [Test]
  public async Task Rewind_ThatThrows_FailsItsRowsForRetry_InsteadOfCompletingThemAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.RewindFailureParksRowsPerspective";
    var coordinator = _rewindCoordinator(streamId, perspectiveName, Guid.CreateVersion7());
    var runner = new RecordingRunner { RunException = new InvalidOperationException("snapshot corrupt") };
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    var (worker, harness, _) = _buildBranchWorker(coordinator, registry, new BranchSetup());
    var work = _branchWork(streamId, perspectiveName, PerspectiveProcessingStatus.RewindRequired);

    await worker.ProcessChannelBatchAsync([work], CancellationToken.None);
    // Completions buffered by a batch are sent at the start of the next one; an empty batch sends them.
    await worker.ProcessChannelBatchAsync([], CancellationToken.None);

    var parked = harness.FailureCapture.Items.Where(i => i.failure.MessageId == work.WorkId).ToList();
    await Assert.That(parked).Count().IsEqualTo(1)
      .Because("the failure is recorded against the row, which is what backs it off and dead-letters it at the bound");
    await Assert.That(parked[0].category).IsEqualTo(WorkCategory.PerspectiveEvent);
    await Assert.That(parked[0].failure.Error).Contains("snapshot corrupt", StringComparison.Ordinal);
    await Assert.That(harness.CompletionCapture.EventWorkIds).DoesNotContain(work.WorkId)
      .Because("a row whose rewind failed applied nothing; completing it would delete the work and lose it");
  }

  [Test]
  public async Task Rewind_ThatThrows_LogsTheAttemptAtWarningWithItsExceptionAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "Deep.RewindFailureWarningPerspective";
    var coordinator = _rewindCoordinator(streamId, perspectiveName, Guid.CreateVersion7());
    var runner = new RecordingRunner { RunException = new InvalidOperationException("snapshot corrupt") };
    var registry = new SingleRunnerRegistry(perspectiveName, runner, [typeof(DeepChannelEvent)]);
    var logger = new FakeLogger<PerspectiveWorker>();
    var (worker, _, _) = _buildBranchWorker(coordinator, registry, new BranchSetup { Logger = logger });

    await worker.ProcessChannelBatchAsync(
      [_branchWork(streamId, perspectiveName, PerspectiveProcessingStatus.RewindRequired)], CancellationToken.None);

    var attempt = logger.Collector.GetSnapshot().Single(r => r.Id.Id == REWIND_FAILED_EVENT_ID);
    await Assert.That(attempt.Level).IsEqualTo(LogLevel.Warning)
      .Because("the rows are retried and dead-lettered at the bound, where the dead-letter record is the Error");
    await Assert.That(attempt.Exception?.Message).IsEqualTo("snapshot corrupt");
  }
}
