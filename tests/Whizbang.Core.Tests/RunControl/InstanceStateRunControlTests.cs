using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.RunControl;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Increment 9: each lifecycle transition is recorded on this instance's own row, so peers and
/// the status surface can observe it — the standby handshake cannot wait for a state nobody can
/// see. Recording rides the run-control broadcast (a transition is not a tick), but must never
/// BREAK a transition: early phases fire before the schema exists, and those failures are
/// expected.
/// </summary>
/// <code-under-test>src/Whizbang.Core/RunControl/InstanceStateRunControl.cs</code-under-test>
[Category("Startup")]
public class InstanceStateRunControlTests {

  private sealed class _stubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = (Guid)TrackedGuid.NewMedo();
    public string ServiceName => "svc";
    public string HostName => "host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = ServiceName,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }

  private sealed class _recordingCoordinator : IWorkCoordinator {
    public List<(Guid InstanceId, string Phase, string? Version)> Recorded { get; } = [];
    public bool Throw { get; init; }
    /// <summary>Thrown in place of the generic failure, for the cancellation contract.</summary>
    public Exception? ThrowSpecific { get; init; }

    public Task<bool> RecordInstanceStateAsync(
        Guid instanceId, string lifecyclePhase, string? libraryVersion = null,
        CancellationToken cancellationToken = default) {
      if (ThrowSpecific is not null) {
        throw ThrowSpecific;
      }
      if (Throw) {
        throw new InvalidOperationException("relation wh_service_instances does not exist");
      }
      Recorded.Add((instanceId, lifecyclePhase, libraryVersion));
      return Task.FromResult(true);
    }

    // Unused surface for this test.
    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<List<PerspectiveCursorInfo>> GetPerspectiveCursorsBatchAsync(IEnumerable<(Guid streamId, string perspectiveName)> requests, CancellationToken cancellationToken = default) => Task.FromResult(new List<PerspectiveCursorInfo>());
    public Task RecordLifecycleCompletionAsync(Guid messageId, string stage, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }

  private static (InstanceStateRunControl Control, _recordingCoordinator Coordinator, _stubInstanceProvider Provider) _build(
      bool withVersion = true, bool coordinatorThrows = false, bool withCoordinator = true,
      Exception? coordinatorThrowsSpecific = null) {
    var coordinator = new _recordingCoordinator {
      Throw = coordinatorThrows,
      ThrowSpecific = coordinatorThrowsSpecific,
    };
    var services = new ServiceCollection();
    if (withCoordinator) {
      services.AddSingleton<IWorkCoordinator>(coordinator);
    }
    var sp = services.BuildServiceProvider();
    var provider = new _stubInstanceProvider();
    var control = new InstanceStateRunControl(
      sp.GetRequiredService<IServiceScopeFactory>(),
      provider,
      withVersion ? new LibraryVersionProvider("0.9.4-alpha.3") : null);
    return (control, coordinator, provider);
  }

  [Test]
  public async Task OnPhase_RecordsThePhaseAndVersionOnThisInstancesRowAsync() {
    var (control, coordinator, provider) = _build();

    await control.OnPhaseAsync(LifecyclePhase.Running, CancellationToken.None);

    await Assert.That(coordinator.Recorded.Count).IsEqualTo(1);
    await Assert.That(coordinator.Recorded[0].InstanceId).IsEqualTo(provider.InstanceId)
      .Because("an instance records ITS OWN state — never another's");
    await Assert.That(coordinator.Recorded[0].Phase).IsEqualTo("Running");
    await Assert.That(coordinator.Recorded[0].Version).IsEqualTo("0.9.4-alpha.3")
      .Because("the version rides along from the generated constant — the same one the ledger records");
  }

  [Test]
  public async Task OnPhase_WithoutAVersionProvider_StillRecordsThePhaseAsync() {
    var (control, coordinator, _) = _build(withVersion: false);

    await control.OnPhaseAsync(LifecyclePhase.Migrating, CancellationToken.None);

    await Assert.That(coordinator.Recorded.Count).IsEqualTo(1);
    await Assert.That(coordinator.Recorded[0].Version).IsNull();
  }

  [Test]
  public async Task OnPhase_WhenRecordingFails_NeverBreaksTheTransitionAsync() {
    var (control, _, _) = _build(coordinatorThrows: true);

    await control.OnPhaseAsync(LifecyclePhase.Connecting, CancellationToken.None);
    // Reaching here IS the assertion: early phases fire before the schema exists, and a
    // recording failure must never fail the lifecycle broadcast that carries it.
  }

  [Test]
  public async Task OnPhase_WithNoCoordinator_IsInertAsync() {
    var (control, coordinator, _) = _build(withCoordinator: false);

    await control.OnPhaseAsync(LifecyclePhase.Running, CancellationToken.None);

    await Assert.That(coordinator.Recorded).IsEmpty()
      .Because("a host with no storage has no instance rows for anyone to observe");
  }

  [Test]
  public async Task OnPhase_CanceledByShutdown_PropagatesRatherThanBeingLoggedAsync() {
    // The catch that keeps a recording failure from breaking a transition is FILTERED on the
    // caller's token. That distinction is the whole design: the write is wrapped in its own
    // timeout, so a slow store cancels the INNER token and is treated as a failure — logged,
    // transition proceeds. Only the caller's own cancellation travels.
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();
    var (control, _, _) = _build(coordinatorThrowsSpecific: new OperationCanceledException());

    await Assert.That(async () => await control.OnPhaseAsync(LifecyclePhase.Running, stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("the host is stopping; recording a phase it is leaving is not worth holding "
             + "shutdown open for");
  }

  [Test]
  public async Task OnPhase_WriteTimingOutWithNoShutdown_IsTreatedAsARecordingFailureAsync() {
    // The other side of that filter, and the reason it is written that way. A store slow enough
    // to blow the write timeout raises the same exception type, with no shutdown behind it. That
    // has to be a logged failure rather than a propagated cancellation: a lifecycle transition
    // must not fail because an observability row was slow to write.
    var (control, _, _) = _build(coordinatorThrowsSpecific: new OperationCanceledException());

    await control.OnPhaseAsync(LifecyclePhase.Connecting, CancellationToken.None);
    // Reaching here IS the assertion — the transition completed despite the cancellation type.
  }
}
