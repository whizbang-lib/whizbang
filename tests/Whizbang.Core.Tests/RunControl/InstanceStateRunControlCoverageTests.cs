using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private int _recordAttempts;

    /// <summary>How many times the recording was attempted. Must stay zero: OnPhaseAsync swallows
    /// every exception, so the throw below is NOT observable to the caller on its own.</summary>
    public int RecordAttempts => Volatile.Read(ref _recordAttempts);

    public Task<bool> RecordInstanceStateAsync(
        Guid instanceId, string lifecyclePhase, string? libraryVersion = null,
        CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _recordAttempts);
      throw new InvalidOperationException("must never be reached without an instance identity");
    }
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

    var logger = new _recordingLogger();
    var control = new InstanceStateRunControl(
      sp.GetRequiredService<IServiceScopeFactory>(),
      instanceProvider: null!,
      logger: logger);

    await control.OnPhaseAsync(LifecyclePhase.Running, CancellationToken.None);

    await Assert.That(coordinator.RecordAttempts).IsEqualTo(0)
      .Because("no identity means no row to record onto — the coordinator must not be asked at all");
    await Assert.That(logger.Entries).IsEmpty()
      .Because("OnPhaseAsync catches every exception and logs it, so a missing guard surfaces as a "
             + "logged failure rather than a thrown one; 'never attempted' has to be read off the "
             + "log being empty, not off the call returning");
  }

  /// <summary>Captures every log line so the guard's silence is observable — without this, the
  /// class's catch-all would absorb an NRE from the missing provider and the test would pass.</summary>
  private sealed class _recordingLogger : ILogger<InstanceStateRunControl> {
    public List<string> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      Entries.Add(formatter(state, exception));
  }
}
