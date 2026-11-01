// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Health;
using Whizbang.Core.Messaging;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The turnkey registration audit: every startup service the wiring composes, pinned at the DI
/// seam where dropping one argument degrades SILENTLY instead of failing. The runner without its
/// elector quietly runs duties on every instance; Assess without its assessor quietly skips;
/// the Ready composite without its contributors quietly fires before subscriptions. None of those
/// throw — which is exactly why each needs a test that does.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
[Category("Startup")]
public class StartupWiringAuditTests {

  private sealed class RecordingElector : IDutyElector {
    private readonly List<string> _asked = [];
    private readonly Lock _lock = new();
    public IReadOnlyList<string> Asked {
      get {
        lock (_lock) {
          return [.. _asked];
        }
      }
    }
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) {
      lock (_lock) {
        _asked.Add(duty);
      }
      // never grants — non-holders act per declaration
      return Task.FromResult(DutyAttempt.Lost(DutyRefusal.Contended, "held by a peer"));
    }
  }

  private sealed class HostLifetime : IHostApplicationLifetime {
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() {
      // never asked to stop here: nothing in this test reaches a stand-down
    }
  }

  private sealed class ServeAssessor : IStartupAssessor {
    public Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken)
      => Task.FromResult(new StartupAssessment(StartupVerdict.Serve, "audit: assessor was consulted"));
  }

  private sealed class DutyProbeStep : IStartupStep {
    private int _executions;
    public int Executions => Volatile.Read(ref _executions);
    public StartupStepDescriptor Descriptor { get; } = new() {
      Name = "AuditDutyProbe",
      RequiredCapability = "audit-duty",
      NonHolderBehavior = NonHolderBehavior.Skip,
    };
    public ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
      Interlocked.Increment(ref _executions);
      return new(new StartupStepReport(StartupStepOutcome.Completed));
    }
  }

  private sealed class GatedContributor : IStartupReadinessContributor {
    public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string ContributorName => "audit-contributor";
    public Task WaitForContributorReadyAsync(CancellationToken cancellationToken)
      => Released.Task.WaitAsync(cancellationToken);
  }

  private static ServiceCollection _collection(Action<ServiceCollection>? before = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    before?.Invoke(services);
    services.AddWhizbangWorkers();
    return services;
  }

  [Test]
  [Timeout(30000)]
  public async Task Runner_ReceivesTheRegisteredElector_AndConsultsItForEveryDutyStepAsync(CancellationToken cancellationToken) {
    var elector = new RecordingElector();
    var probe = new DutyProbeStep();
    var services = _collection(s => s.AddSingleton<IDutyElector>(elector));
    services.AddSingleton<IStartupStep>(probe);
    await using var sp = services.BuildServiceProvider();
    sp.GetRequiredService<ISchemaReadyGate>().MarkReady();

    var results = await sp.GetRequiredService<StartupPipelineRunner>().RunAsync(cancellationToken);

    await Assert.That(elector.Asked).Contains(StartupDuties.MAINTAINER)
      .Because("the wiring must hand the runner the registered elector — without it, the rewrite "
             + "duty silently degrades to running on every instance");
    await Assert.That(elector.Asked).Contains("audit-duty");
    await Assert.That(probe.Executions).IsEqualTo(0)
      .Because("the elector refused, and the probe declared Skip");
    await Assert.That(results.Single(r => r.Name == "AuditDutyProbe").Reason)
      .IsEqualTo("capability not held");
  }

  [Test]
  [Timeout(30000)]
  public async Task Runner_WithoutAnElector_ADutyDegradesToASharedCapabilityAsync(CancellationToken cancellationToken) {
    var probe = new DutyProbeStep();
    var services = _collection();
    services.AddSingleton<IStartupStep>(probe);
    await using var sp = services.BuildServiceProvider();
    sp.GetRequiredService<ISchemaReadyGate>().MarkReady();

    _ = await sp.GetRequiredService<StartupPipelineRunner>().RunAsync(cancellationToken);

    await Assert.That(probe.Executions).IsEqualTo(1)
      .Because("the documented degradation: with no elector registered, a duty behaves as a "
             + "shared capability — survivable because the framework's exclusive steps are "
             + "individually idempotent and separately guarded");
  }

  [Test]
  [Timeout(30000)]
  public async Task AssessStep_ReceivesTheRegisteredAssessorAsync(CancellationToken cancellationToken) {
    var services = _collection(s => s.AddSingleton<IStartupAssessor>(new ServeAssessor()));
    await using var sp = services.BuildServiceProvider();
    sp.GetRequiredService<ISchemaReadyGate>().MarkReady();

    var results = await sp.GetRequiredService<StartupPipelineRunner>().RunAsync(cancellationToken);

    var assess = results.Single(r => r.Name == FrameworkStartupSteps.ASSESS);
    await Assert.That(assess.Outcome).IsEqualTo(StartupStepOutcome.Completed);
    await Assert.That(assess.Reason).IsEqualTo("audit: assessor was consulted")
      .Because("the wiring must hand the step the driver-registered assessor — without it, "
             + "Assess silently skips and the never-downgrade verdict never runs");
  }

  [Test]
  [Timeout(30000)]
  public async Task LifecyclePhaseWorker_GetsBothGatesFromDI_AndHoldsAtAcceptingCommandsAsync(CancellationToken cancellationToken) {
    var services = _collection();
    await using var sp = services.BuildServiceProvider();
    var lifecycle = sp.GetRequiredService<IWhizbangLifecycleState>();
    var schemaGate = sp.GetRequiredService<ISchemaReadyGate>();
    var readGate = sp.GetRequiredService<IReadModelsReadyGate>();

    // Constructed FROM the container, exactly as the hosted registration does — proving DI
    // resolves the optional read gate rather than defaulting it away.
    var worker = ActivatorUtilities.CreateInstance<LifecyclePhaseWorker>(sp);
    await worker.StartAsync(cancellationToken);
    try {
      schemaGate.MarkReady();
      while (lifecycle.Phase != LifecyclePhase.AcceptingCommands) {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(20, cancellationToken);
      }
      await Task.Delay(150, cancellationToken);
      await Assert.That(lifecycle.Phase).IsEqualTo(LifecyclePhase.AcceptingCommands)
        .Because("the read gate resolved from DI is what holds the ladder here — if the wiring "
               + "dropped it, the phase would race straight to Running");

      readGate.MarkReady();
      while (lifecycle.Phase != LifecyclePhase.Running) {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(20, cancellationToken);
      }
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }
  }

  [Test]
  [Timeout(30000)]
  public async Task ReadyService_ReceivesTheDIRegisteredContributorsAsync(CancellationToken cancellationToken) {
    var contributor = new GatedContributor();
    var services = _collection(s => s.AddSingleton<IStartupReadinessContributor>(contributor));
    await using var sp = services.BuildServiceProvider();
    sp.GetRequiredService<ISchemaReadyGate>().MarkReady();
    _ = await sp.GetRequiredService<StartupPipelineRunner>().RunAsync(cancellationToken);

    var readyService = sp.GetRequiredService<StartupReadyService>();
    var signal = sp.GetRequiredService<IStartupReadySignal>();
    var started = readyService.StartedAsync(cancellationToken);

    await Task.Delay(150, cancellationToken);
    await Assert.That(signal.IsReady).IsFalse()
      .Because("the composite must include DI-registered contributors — without them, Ready "
             + "fires before the transports have subscribed");

    contributor.Released.SetResult();
    await started.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    await Assert.That(signal.IsReady).IsTrue();
  }

  [Test]
  public async Task HealthSources_IncludeTheStartupPipelineSourceAsync() {
    var services = _collection();
    await using var sp = services.BuildServiceProvider();

    var sources = sp.GetServices<IWhizbangHealthSource>().ToList();

    await Assert.That(sources.Any(s => s is StartupPipelineHealthSource)).IsTrue()
      .Because("'why is this pod not ready' must be answerable from the health surface");
  }

  [Test]
  public async Task RunControlPlane_RecordsTheInstanceRow_ThroughTheStageParticipantAlone_WithTheLibraryVersionAsync(
      CancellationToken cancellationToken) {
    var writes = new TransitionRecorder();
    var services = _collection(s => {
      s.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
        new Whizbang.Core.Observability.ServiceInstanceProvider(Guid.CreateVersion7(), "audit-svc", "audit-host", processId: 1));
      s.AddSingleton<Whizbang.Core.Observability.ILibraryVersionProvider>(
        new Whizbang.Core.Observability.LibraryVersionProvider("7.7.7"));
    });
    services.AddScoped<IWorkCoordinator>(_ => writes);
    await using var sp = services.BuildServiceProvider();
    await sp.GetRequiredService<Whizbang.Core.Startup.IStartupStageReporter>().InstanceRegisteredAsync(cancellationToken);

    foreach (var participant in sp.GetServices<IWhizbangRunControl>()) {
      await participant.OnPhaseAsync(LifecyclePhase.StandingBy, cancellationToken);
    }

    await Assert.That(writes.Calls).IsEquivalentTo(["transition StandingBy 7.7.7"])
      .Because("one write per transition (#1636): the standby handshake's StandingBy reaches the row through the stage "
        + "record alone, with the version the host registered");
  }

  /// <summary>Records every instance-row write a participant makes, by kind.</summary>
  private sealed class TransitionRecorder : IWorkCoordinator {
    public List<string> Calls { get; } = [];

    public Task<bool> RecordInstanceTransitionAsync(
        Whizbang.Core.Startup.InstanceTransition transition, CancellationToken cancellationToken = default) {
      Calls.Add($"transition {transition.Phase} {transition.LibraryVersion}");
      return Task.FromResult(true);
    }

    public Task<bool> RecordInstanceStateAsync(
        Guid instanceId, string lifecyclePhase, string? libraryVersion = null, CancellationToken cancellationToken = default) {
      Calls.Add($"state {lifecyclePhase}");
      return Task.FromResult(true);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PartitionRecomputeResult> RecomputePartitionNumbersAsync(int partitionCount, CancellationToken cancellationToken = default) => Task.FromResult(new PartitionRecomputeResult());
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task<IReadOnlyList<MaintenanceResult>> PerformMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MaintenanceResult>>([]);
  }

  [Test]
  [Timeout(30000)]
  public async Task TheReadyServiceAndTheStandbyWatcher_RunOnTheHostsRegisteredClockAsync(CancellationToken cancellationToken) {
    // Both are built by factories in the worker pipeline. A factory that dropped the clock, or took
    // the system clock instead of the registered one, would put the service on real time with
    // nothing to show for it: each arms its first timer on the host's clock, or the test hangs.
    var clock = new ProbeClock();
    var services = _collection(s => {
      s.AddSingleton<TimeProvider>(clock);
      s.AddSingleton<IHostApplicationLifetime, HostLifetime>();   // the host's own; a bare collection has none
    });
    await using var sp = services.BuildServiceProvider();
    sp.GetRequiredService<ISchemaReadyGate>().MarkReady();
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    var ready = sp.GetRequiredService<StartupReadyService>();
    var started = ready.StartedAsync(cts.Token);   // no run has started, so it waits and probes
    var readyProbe = await clock.NextArmedAsync(cancellationToken);
    await Assert.That(readyProbe).IsGreaterThan(ProbeClock.Origin)
      .Because("the ready service's narration probe is armed on the registered clock");
    await cts.CancelAsync();
    await started;

    var watcher = (StandbyWatcher)services
      .Single(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory?.Method.ReturnType == typeof(StandbyWatcher))
      .ImplementationFactory!(sp);
    await watcher.StartAsync(cancellationToken);
    var poll = await clock.NextArmedAsync(cancellationToken);
    await Assert.That(poll).IsEqualTo(ProbeClock.Origin + sp.GetRequiredService<StandbyWatcherOptions>().PollInterval)
      .Because("the watcher's poll is armed on the registered clock, at its configured interval");
    await watcher.StopAsync(CancellationToken.None);
    watcher.Dispose();
  }

  [Test]
  public async Task HostedServices_IncludeEveryStartupWorkerAsync() {
    var services = _collection();

    var hosted = services
      .Where(d => d.ServiceType == typeof(IHostedService))
      .Select(d => d.ImplementationType ?? d.ImplementationFactory?.Method.ReturnType)
      .Where(t => t is not null)
      .Select(t => t!.Name)
      .ToList();

    foreach (var expected in new[] {
        nameof(LifecyclePhaseWorker), nameof(ReadModelsReadyDriver),
        nameof(StartupPipelineWorker), nameof(StartupReadyService), nameof(StandbyWatcher) }) {
      await Assert.That(hosted).Contains(expected)
        .Because($"{expected} is part of the turnkey startup arrangement — a registration "
               + "dropped here fails silently at runtime, never at build");
    }
  }
}
