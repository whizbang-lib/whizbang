using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Service-wide settledness, the signal auto-repair is gated on.
/// </summary>
/// <remarks>
/// <para>
/// A service runs many instances against one shared inbox. An instance that has finished its own
/// claimed streams reads zero locally while peers are still draining, so an instance-scoped count
/// cannot answer "has this service settled" — and repairing off that local view re-requests events
/// its own siblings are actively processing.
/// </para>
/// <para>
/// The distinction that matters most is between settled and UNMEASURED. "Nothing outstanding" and
/// "nobody looked" are the same number and opposite facts; defaulting the second to the first
/// re-enables exactly the behavior the gate exists to prevent.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Messaging/IWorkCoordinator.cs</code-under-test>
[Category("Messaging")]
public class ServiceBacklogTests {

  [Test]
  public async Task NothingQueuedAndNothingLeasedIsSettledAsync() {
    var b = new ServiceBacklog { UnprocessedInboxRows = 0, ActiveLeasedRows = 0 };
    await Assert.That(b.IsSettled).IsTrue()
      .Because("a genuinely quiet service is the one case where a residual deficit really is "
             + "missing data rather than work in flight");
  }

  [Test]
  public async Task QueuedWorkMeansNotSettledAsync() {
    var b = new ServiceBacklog { UnprocessedInboxRows = 1, ActiveLeasedRows = 0 };
    await Assert.That(b.IsSettled).IsFalse()
      .Because("one queued row is enough — the events counted as missing may be exactly the ones "
             + "waiting, and re-requesting them lengthens the queue that produced the deficit");
  }

  [Test]
  public async Task APeersLeaseMeansNotSettledEvenWithAnEmptyQueueAsync() {
    var b = new ServiceBacklog { UnprocessedInboxRows = 0, ActiveLeasedRows = 1 };
    await Assert.That(b.IsSettled).IsFalse()
      .Because("this is the case neither depth nor lag catches: THIS instance is idle and the queue "
             + "is drained, but a sibling holds the rows mid-dispatch — the storm returning through "
             + "whichever replica happened to be free");
  }

  [Test]
  public async Task BothNonZeroIsNotSettledAsync() {
    var b = new ServiceBacklog { UnprocessedInboxRows = 500, ActiveLeasedRows = 25 };
    await Assert.That(b.IsSettled).IsFalse();
  }

  [Test]
  public async Task TheDefaultCoordinatorReportsUnmeasuredNotSettledAsync() {
    IWorkCoordinator coord = new _DefaultingCoordinator();

    var result = await coord.CountServiceBacklogAsync();

    await Assert.That(result).IsNull()
      .Because("a backend that cannot answer must return null so callers gate CLOSED; returning an "
             + "all-zero backlog would read as settled and silently re-enable auto-repair on every "
             + "store that never implemented the count");
  }

  /// <summary>A coordinator implementing only what the interface requires.</summary>
  private sealed class _DefaultingCoordinator : IWorkCoordinator {
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest req, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
    public Task<bool> RecordHeartbeatAsync(HeartbeatRequest request, CancellationToken ct = default) =>
      Task.FromResult(true);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default) =>
      Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken ct = default) =>
      Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken ct = default) =>
      Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken ct = default) => Task.CompletedTask;
  }
}
