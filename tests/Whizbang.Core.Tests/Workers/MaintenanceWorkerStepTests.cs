using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Helpers;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Packages add work to the maintenance cycle as steps, and the cycle runs them.
/// </summary>
/// <remarks>
/// A step inherits everything the cycle already decided: the schema is ready, the service has settled,
/// and it is the configured time. These lock that a registered step runs in the cycle, in the cycle's
/// scope, in order, and that one failing step neither stops the others nor the cycle.
/// </remarks>
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerStepTests {

  private sealed class RecordingStep(string name, List<string> log, Exception? throws = null) : IMaintenanceStep {
    public string Name => name;
    public bool ResolvedAScopedService { get; private set; }
    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) {
      ResolvedAScopedService = services.GetService<ScopeMarker>() is not null;
      lock (log) { log.Add(name); }
      return throws is null ? Task.CompletedTask : Task.FromException(throws);
    }
  }

  private sealed class ScopeMarker;

  private sealed class MinimalCoordinator : IWorkCoordinator {
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken cancellationToken = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }

  private static (MaintenanceWorker Worker, CapturingLogger<MaintenanceWorker> Logger) _build(params IMaintenanceStep[] steps) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(new MinimalCoordinator());
    services.AddScoped<ScopeMarker>();
    foreach (var step in steps) {
      services.AddSingleton<IMaintenanceStep>(step);
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new CapturingLogger<MaintenanceWorker>();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      logger);
    return (worker, logger);
  }

  [Test]
  public async Task Cycle_RunsEveryRegisteredStep_InRegistrationOrderAsync() {
    var log = new List<string>();
    var (worker, _) = _build(new RecordingStep("first", log), new RecordingStep("second", log));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(log.SequenceEqual(["first", "second"])).IsTrue()
      .Because("a step added by a package must run as part of the cycle, not merely be registered");
  }

  [Test]
  public async Task Step_ReceivesTheCyclesScopedProviderAsync() {
    var step = new RecordingStep("scoped", []);
    var (worker, _) = _build(step);

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(step.ResolvedAScopedService).IsTrue()
      .Because("a step resolves scoped services such as the work coordinator from the cycle's scope");
  }

  [Test]
  public async Task FailingStep_IsLogged_AndTheNextStepStillRunsAsync() {
    var log = new List<string>();
    var (worker, logger) = _build(
      new RecordingStep("broken", log, new InvalidOperationException("boom")),
      new RecordingStep("after", log));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(log).Contains("after")
      .Because("one broken step must not switch the others off");
    await Assert.That(logger.Snapshot().Any(e => e.Level == LogLevel.Warning && e.Message.Contains("broken", StringComparison.Ordinal)))
      .IsTrue().Because("an operator must see which step failed");
  }

  [Test]
  public async Task CanceledStep_PropagatesCancellationAsync() {
    var (worker, _) = _build(new RecordingStep("canceled", [], new OperationCanceledException()));

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("cancellation is shutdown, never a step failure to log and move past");
  }
}
