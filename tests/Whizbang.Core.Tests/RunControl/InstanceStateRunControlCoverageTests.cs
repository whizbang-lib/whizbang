using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.RunControl;

namespace Whizbang.Core.Tests.RunControl;

/// <summary>
/// Targeted coverage for <see cref="InstanceStateRunControl.OnPhaseAsync"/>'s no-identity guard —
/// a host with no <see cref="IServiceInstanceProvider"/>, which the broader
/// <see cref="InstanceStateRunControlTests"/> suite never constructs (every scenario there supplies
/// one).
/// </summary>
/// <code-under-test>src/Whizbang.Core/RunControl/InstanceStateRunControl.cs</code-under-test>
[Category("Startup")]
public class InstanceStateRunControlCoverageTests {

  private sealed class _throwingCoordinator : IWorkCoordinator {
    public Task<bool> RecordInstanceStateAsync(
        Guid instanceId, string lifecyclePhase, string? libraryVersion = null,
        CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("must never be reached without an instance identity");
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

  [Test]
  public async Task OnPhase_WithNoInstanceProvider_NeverAttemptsARecordingAsync() {
    // A host with no instance identity has no row to record a transition onto. If this guard were
    // missing, the coordinator would be asked to record state for a null/default instance id —
    // either throwing (breaking the lifecycle broadcast the class exists to never break) or writing
    // a bogus row peers could mistake for a real instance.
    var coordinator = new _throwingCoordinator();
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    using var sp = services.BuildServiceProvider();

    var control = new InstanceStateRunControl(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: null!);

    // Reaching here without the throwing coordinator ever being invoked IS the assertion.
    await control.OnPhaseAsync(LifecyclePhase.Running, CancellationToken.None);
  }
}
