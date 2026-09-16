using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A database failure inside a drain batch is a fact about the database at that moment, not about
/// the worker. The drain pass reports it once with the classification and the streams it was
/// carrying, hands those streams' unstarted rows back so a sibling can take them, waits out a
/// backoff on the worker's own clock, and the loop takes the next batch.
/// </summary>
/// <remarks>
/// Before this, the loop logged and rethrew: the exception left <c>ExecuteAsync</c> and the host's
/// default <c>BackgroundServiceExceptionBehavior</c> (<c>StopHost</c>) stopped the process over a
/// deadlock against a sibling instance's schema DDL — a failure the next attempt would have won.
/// </remarks>
public partial class PerspectiveWorkerDeepPathDrainTests {
  private const int LEASES_RELEASED_EVENT_ID = 69;
  private const int LEASES_NOT_RELEASED_EVENT_ID = 70;

  private sealed record FailedBatchRun(
    DrainRunner Runner,
    EventIdSignalingLogger<PerspectiveWorker> Logger,
    DrainWorkCoordinator Coordinator,
    Guid StreamId);

  /// <summary>
  /// Runs a drain batch whose fetch fails, then a second batch that succeeds, and returns what each
  /// collaborator saw. Every wait is on a signal the worker itself emits: the report the loop wrote,
  /// and the runner the batch after the failure reached.
  /// </summary>
  private static async Task<FailedBatchRun> _runBatchAfterAFailedFetchAsync(
      Exception firstFetchFailure, int expectedEventId, Exception? releaseFailure = null) {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator { ReleaseException = releaseFailure };
    // The first fetch fails; the second finds the rows.
    coordinator.EnqueueStreamEventsError(firstFetchFailure);
    coordinator.EnqueueStreamEvents([_raw(streamId, eventId, Guid.CreateVersion7())]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(eventId, new DrainDeepEvent("after-the-failure"))]);
    var runner = new DrainRunner();
    var registry = _registry(runner);
    var logger = new EventIdSignalingLogger<PerspectiveWorker>();
    var time = new FakeTimeProvider();
    var (worker, harness, _) = _createWorker(coordinator, eventStore, registry, logger: logger, timeProvider: time);
    using var cts = new CancellationTokenSource();

    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await logger.WhenLoggedAsync(expectedEventId, TimeSpan.FromSeconds(10));

    // The loop is alive and backing off on the fake clock, not stopped.
    await Assert.That(worker.ExecuteTask!.IsCompleted).IsFalse()
      .Because("a failure inside one batch must never end the worker: the host stops with it");

    // The rows are unassigned again (or their leases lapse), so the stream is offered afresh; here
    // the test plays the claim loop, and steps the clock through the worker's backoff.
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await FakeClockPump.StepUntilAsync(time, runner.FirstRunWithEvents.WaitAsync(TimeSpan.FromSeconds(10)));
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }

    var reported = logger.LinesWith(expectedEventId);
    await Assert.That(reported).Count().IsEqualTo(1);
    await Assert.That(reported[0].Level).IsEqualTo(LogLevel.Error);
    await Assert.That(reported[0].Message).Contains(streamId.ToString(), StringComparison.Ordinal)
      .Because("an operator reads which streams the lost batch was carrying from the one line");
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(1)
      .Because("the batch after the failure is applied as if nothing had happened");
    await Assert.That(coordinator.GetStreamEventsCallCount).IsEqualTo(2);
    return new FailedBatchRun(runner, logger, coordinator, streamId);
  }

  [Test]
  public async Task DrainMode_TransientDatabaseFailure_IsReportedOnceAndTheLoopContinuesAsync() {
    var run = await _runBatchAfterAFailedFetchAsync(
      FakeDbException.WithSqlState("40P01", message: "deadlock detected"),
      PerspectiveWorker.TRANSIENT_BATCH_FAILURE_EVENT_ID);

    var reported = run.Logger.LinesWith(PerspectiveWorker.TRANSIENT_BATCH_FAILURE_EVENT_ID)[0];
    await Assert.That(reported.Message).Contains(TransientDatabaseFailure.DEADLOCK, StringComparison.Ordinal);
    await Assert.That(reported.Message).Contains("40P01", StringComparison.Ordinal);
    await Assert.That(reported.Exception).IsTypeOf<FakeDbException>();
    await Assert.That(run.Logger.LinesWith(PerspectiveWorker.UNEXPECTED_BATCH_FAILURE_EVENT_ID)).IsEmpty()
      .Because("a deadlock is not a defect, and an operator paged for one would find nothing to fix");

    var released = run.Coordinator.PerspectiveLeaseReleases.Single();
    await Assert.That(released).IsEquivalentTo([run.StreamId])
      .Because("the batch's streams go back to unassigned, so a sibling takes them now rather than "
             + "after the lease lapses");
    await Assert.That(run.Logger.LinesWith(LEASES_RELEASED_EVENT_ID)).Count().IsEqualTo(1);
  }

  [Test]
  public async Task DrainMode_FailureThatIsNotTheDatabases_IsReportedAsADefectAndTheLoopContinuesAsync() {
    var run = await _runBatchAfterAFailedFetchAsync(
      new InvalidOperationException("a defect in the fetch"),
      PerspectiveWorker.UNEXPECTED_BATCH_FAILURE_EVENT_ID);

    var reported = run.Logger.LinesWith(PerspectiveWorker.UNEXPECTED_BATCH_FAILURE_EVENT_ID)[0];
    await Assert.That(reported.Exception).IsTypeOf<InvalidOperationException>();
    await Assert.That(run.Logger.LinesWith(PerspectiveWorker.TRANSIENT_BATCH_FAILURE_EVENT_ID)).IsEmpty()
      .Because("the loop survives a defect too, but it must not be filed as the database's doing");
  }

  [Test]
  public async Task DrainMode_AStoreThatCannotReleaseTheLeases_SaysSoAndStillContinuesAsync() {
    // Release is defaulted on IWorkCoordinator: a store that never implemented it throws, and the
    // batch's leases then lapse on their own, which is what happened before any of this existed.
    var run = await _runBatchAfterAFailedFetchAsync(
      FakeDbException.WithSqlState("40P01"),
      PerspectiveWorker.TRANSIENT_BATCH_FAILURE_EVENT_ID,
      releaseFailure: new NotImplementedException("this store does not release leases"));

    var noted = run.Logger.LinesWith(LEASES_NOT_RELEASED_EVENT_ID);
    await Assert.That(noted).Count().IsEqualTo(1);
    await Assert.That(noted[0].Level).IsEqualTo(LogLevel.Warning)
      .Because("leases that lapse instead of being handed back cost a sibling the lease duration, "
             + "which is worth a line an operator can see");
    await Assert.That(noted[0].Exception).IsTypeOf<NotImplementedException>();
    await Assert.That(run.Logger.LinesWith(LEASES_RELEASED_EVENT_ID)).IsEmpty();
  }
}
