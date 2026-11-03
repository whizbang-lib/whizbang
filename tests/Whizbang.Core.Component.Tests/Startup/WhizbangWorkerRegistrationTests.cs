// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// A worker is hosted with the point it starts at (#1636), and the host starts it there: the worker holds no gate code
/// of its own. <c>AddWhizbangWorker</c> starts it once the instance reaches a startup stage, Accept commands unless
/// another is named, so the easy registration is the safe one; <c>AddWhizbangWorkerAtSchemaReady</c> starts it when the
/// schema-ready gate opens, at the end of Coordinate, for the work that keeps an instance alive and coordinated.
/// </summary>
/// <remarks>
/// Every wait is on a signal: the gates are opened by the test, the service records at the moment it is started whether
/// its start point had been reached, and the wrapper's <c>Started</c> signal is what a test awaits.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Startup/WhizbangWorkerServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/GatedHostedService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/SchemaReadyGatedService.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageGatedService.cs</code-under-test>
[Category("Startup")]
public class WhizbangWorkerRegistrationTests {
  private static StartupStageSnapshot _at(StartupStage stage) => new(stage, "message", false, DateTimeOffset.UnixEpoch);

  [Test]
  public async Task AddWhizbangWorker_StartsAtAcceptCommandsByDefault_AndHostsTheRegisteredSingletonAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangWorker<Probe>();
    services.AddWhizbangWorker<Probe>();
    await using var provider = services.BuildServiceProvider();

    var hosted = provider.GetServices<IHostedService>().ToList();
    await Assert.That(hosted.Count).IsEqualTo(1).Because("registering the same worker again changes nothing");
    var gated = (StartupStageGatedService<Probe>)hosted[0];
    await Assert.That(gated.Stage).IsEqualTo(StartupStage.AcceptingCommands)
      .Because("a worker that names no stage starts when the instance is eligible for work");
    await Assert.That(gated.Inner).IsSameReferenceAs(provider.GetRequiredService<Probe>())
      .Because("the hosted worker is the singleton everything else resolves");
  }

  [Test]
  public async Task AddWhizbangWorker_AtANamedStage_WithAFactory_HostsWhatTheFactoryReturnsAsync() {
    var services = new ServiceCollection();
    var probe = new Probe();
    services.AddWhizbangWorker(StartupStage.Running, _ => probe);
    await using var provider = services.BuildServiceProvider();

    var gated = provider.GetServices<IHostedService>().OfType<StartupStageGatedService<Probe>>().Single();
    await Assert.That(gated.Stage).IsEqualTo(StartupStage.Running);
    await Assert.That(gated.Inner).IsSameReferenceAs(probe);
  }

  [Test]
  public async Task AddWhizbangWorkerAtSchemaReady_HostsTheWorkerBehindTheSchemaReadyGateAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangWorkerAtSchemaReady<Probe>();
    services.AddWhizbangWorkerAtSchemaReady<Probe>();
    var other = new Other();
    services.AddWhizbangWorkerAtSchemaReady(_ => other);
    await using var provider = services.BuildServiceProvider();

    var hosted = provider.GetServices<IHostedService>().ToList();
    await Assert.That(hosted.Count).IsEqualTo(2).Because("registering the same worker again changes nothing");
    await Assert.That(hosted.OfType<SchemaReadyGatedService<Probe>>().Single().Inner)
      .IsSameReferenceAs(provider.GetRequiredService<Probe>());
    await Assert.That(hosted.OfType<SchemaReadyGatedService<Other>>().Single().Inner).IsSameReferenceAs(other);
  }

  [Test]
  [Timeout(30000)]
  public async Task AtSchemaReady_TheWorkerStartsOnlyOnceTheGateOpensAsync(CancellationToken cancellationToken) {
    var schemaReady = new SchemaReadyGate();
    var inner = new Probe { StartedWhen = () => schemaReady.IsReady };
    using var wrapper = new SchemaReadyGatedService<Probe>(inner, schemaReady);

    await wrapper.StartAsync(cancellationToken);
    schemaReady.MarkReady();
    await wrapper.Started.WaitAsync(cancellationToken);

    await Assert.That(inner.StartPointReachedWhenStarted).IsTrue()
      .Because("the worker is started only after the schema-ready gate opened");
    await wrapper.StopAsync(cancellationToken);
    await Assert.That(inner.Stops).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task AtSchemaReady_StoppedBeforeTheGateOpens_TheWorkerIsNeverStartedAsync(CancellationToken cancellationToken) {
    var inner = new Probe();
    using var wrapper = new SchemaReadyGatedService<Probe>(inner, new SchemaReadyGate());
    await wrapper.StartAsync(cancellationToken);

    await wrapper.StopAsync(cancellationToken);

    await Assert.That(wrapper.ExecuteTask!.IsCompleted).IsTrue();
    await Assert.That(inner.Starts).IsEqualTo(0);
    await Assert.That(inner.Stops).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task AtSchemaReady_WithNoSchemaToInitialize_TheWorkerStartsWithTheHostAsync(CancellationToken cancellationToken) {
    var inner = new Probe();
    using var wrapper = new SchemaReadyGatedService<Probe>(inner, SchemaReadyGate.AlreadyReady());

    await wrapper.StartAsync(cancellationToken);
    await wrapper.Started.WaitAsync(cancellationToken);

    await Assert.That(inner.Starts).IsEqualTo(1);
    await wrapper.StopAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task AnEligibleStage_WithNoStageGate_WaitsForTheSchemaReadyGateAsync(CancellationToken cancellationToken) {
    // A host composed without the worker pipeline records no stages; the schema-ready gate is the latest point it has,
    // so a worker that needs Accept commands still never starts on an unmigrated schema.
    var schemaReady = new SchemaReadyGate();
    var inner = new Probe { StartedWhen = () => schemaReady.IsReady };
    using var wrapper = new StartupStageGatedService<Probe>(inner, StartupStage.AcceptingCommands, gate: null, schemaReady);

    await wrapper.StartAsync(cancellationToken);
    schemaReady.MarkReady();
    await wrapper.Started.WaitAsync(cancellationToken);

    await Assert.That(inner.StartPointReachedWhenStarted).IsTrue();
    await wrapper.StopAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task AStageBeforeTheGate_WithNoStageGate_StartsWithTheHostAsync(CancellationToken cancellationToken) {
    // Coordinate and the stages before it are what the schema-ready gate waits for, so without a stage gate a worker
    // that needs one of them starts with the host, as it did before it was wrapped.
    var inner = new Probe();
    using var wrapper = new StartupStageGatedService<Probe>(inner, StartupStage.Coordinate, gate: null, new SchemaReadyGate());

    await wrapper.StartAsync(cancellationToken);
    await wrapper.Started.WaitAsync(cancellationToken);

    await Assert.That(inner.Starts).IsEqualTo(1);
    await wrapper.StopAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task AnEligibleStage_WithAStageGate_IgnoresTheSchemaReadyGateAsync(CancellationToken cancellationToken) {
    // The stage gate is the authority when there is one: the schema-ready gate opening does not start a worker that
    // waits for Accept commands, which is recorded only after it.
    var schemaReady = new SchemaReadyGate();
    var gate = new StartupStageGate(schemaReady);
    var inner = new Probe { StartedWhen = () => gate.HasReached(StartupStage.AcceptingCommands) };
    using var wrapper = new StartupStageGatedService<Probe>(inner, StartupStage.AcceptingCommands, gate, schemaReady);

    await wrapper.StartAsync(cancellationToken);
    schemaReady.MarkReady();
    await gate.OnStageChangedAsync(_at(StartupStage.AcceptingCommands), true, cancellationToken);
    await wrapper.Started.WaitAsync(cancellationToken);

    await Assert.That(inner.StartPointReachedWhenStarted).IsTrue();
    await wrapper.StopAsync(cancellationToken);
  }

  [Test]
  public async Task EachWrapper_SaysWhatItStartsAndWhereAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangWorker<Probe>();
    services.AddWhizbangWorkerAtSchemaReady<Other>();
    await using var provider = services.BuildServiceProvider();

    var described = provider.GetServices<IHostedService>().Cast<GatedHostedService>()
      .Select(g => $"{g.Service.GetType().Name} at {g.StartPoint}").ToList();

    await Assert.That(described).IsEquivalentTo(["Probe at Accept commands", "Other at schema ready"])
      .Because("every wrapper names the service it starts and its start point, without the caller knowing its type");
  }

  [Test]
  [Timeout(30000)]
  public async Task StoppedBeforeItsStartPoint_AWorkerThatAsksIsToldItsStartWasAbandonedAsync(CancellationToken cancellationToken) {
    // A worker whose startup others wait on (the transport consumer's subscriptions, the perspective worker's startup
    // scan) settles that signal when the host stops before its start point, so no waiter hangs on a start that will
    // never come.
    var inner = new Abandonable();
    using var wrapper = new StartupStageGatedService<Abandonable>(
      inner, StartupStage.AcceptingCommands, new StartupStageGate(new SchemaReadyGate()));
    await wrapper.StartAsync(cancellationToken);

    await wrapper.StopAsync(cancellationToken);

    await Assert.That(inner.Abandoned.Task.IsCanceled).IsTrue()
      .Because("the worker was told, and settled its signal as canceled");
    await Assert.That(inner.Starts).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task Started_AWorkerIsNeverToldItsStartWasAbandonedAsync(CancellationToken cancellationToken) {
    var inner = new Abandonable();
    using var wrapper = new SchemaReadyGatedService<Abandonable>(inner, SchemaReadyGate.AlreadyReady());
    await wrapper.StartAsync(cancellationToken);
    await wrapper.Started.WaitAsync(cancellationToken);

    await wrapper.StopAsync(cancellationToken);

    await Assert.That(inner.Abandoned.Task.IsCompleted).IsFalse();
    await Assert.That(inner.Starts).IsEqualTo(1);
  }

  [Test]
  [Timeout(30000)]
  public async Task AHostWithNoStorage_StartsItsDataPlaneAtAcceptCommandsAsync(CancellationToken cancellationToken) {
    // Nothing to record: no coordinator keeps an instance row, so the gate does not wait for one (#1636).
    var (reporter, gate) = _reporterAndGate(coordinator: null);
    var inner = new Probe { StartedWhen = () => gate.HasReached(StartupStage.AcceptingCommands) };
    using var wrapper = new StartupStageGatedService<Probe>(inner, StartupStage.AcceptingCommands, gate);
    await wrapper.StartAsync(cancellationToken);

    await reporter.EnterAsync(StartupStage.AcceptingCommands, "accepting", cancellationToken);
    await wrapper.Started.WaitAsync(cancellationToken);

    await Assert.That(inner.StartPointReachedWhenStarted).IsTrue();
    await wrapper.StopAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task AFailedAcceptCommandsWrite_HoldsTheDataPlane_UntilTheNextHeartbeatWritesItAsync(
      CancellationToken cancellationToken) {
    // The row write fails, so the row still records the stage before and the instance is not eligible: the data plane
    // waits. The retry is the next accepted heartbeat, whose registration signal writes the held stage (#1636).
    var coordinator = new FlakyRecorder { Fails = true };
    var (reporter, gate) = _reporterAndGate(coordinator);
    var inner = new Probe { StartedWhen = () => gate.HasReached(StartupStage.AcceptingCommands) };
    using var wrapper = new StartupStageGatedService<Probe>(inner, StartupStage.AcceptingCommands, gate);
    await wrapper.StartAsync(cancellationToken);
    await reporter.InstanceRegisteredAsync(cancellationToken);

    await reporter.EnterAsync(StartupStage.AcceptingCommands, "accepting", cancellationToken);

    await Assert.That(gate.HasReached(StartupStage.AcceptingCommands)).IsFalse()
      .Because("the write failed, so the row does not say the instance is eligible yet");
    await Assert.That(inner.Starts).IsEqualTo(0);

    coordinator.Fails = false;
    await reporter.InstanceRegisteredAsync(cancellationToken);   // the next accepted heartbeat
    await wrapper.Started.WaitAsync(cancellationToken);

    await Assert.That(inner.StartPointReachedWhenStarted).IsTrue();
    await Assert.That(coordinator.Written).IsEqualTo(1);
    await wrapper.StopAsync(cancellationToken);
  }

  [Test]
  public async Task NullArguments_AreRefusedAsync() {
    await Assert.That(() => new SchemaReadyGatedService<Probe>(null!, new SchemaReadyGate())).Throws<ArgumentNullException>();
    await Assert.That(() => new SchemaReadyGatedService<Probe>(new Probe(), null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new StartupStageGatedService<Probe>(new Probe(), StartupStage.Running, null, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => WhizbangWorkerServiceCollectionExtensions.AddWhizbangWorker<Probe>(null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => WhizbangWorkerServiceCollectionExtensions.AddWhizbangWorker<Probe>(
      null!, StartupStage.Running, _ => new Probe())).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangWorker<Probe>(StartupStage.Running, null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => WhizbangWorkerServiceCollectionExtensions.AddWhizbangWorkerAtSchemaReady<Probe>(null!))
      .Throws<ArgumentNullException>();
    await Assert.That(() => WhizbangWorkerServiceCollectionExtensions.AddWhizbangWorkerAtSchemaReady<Probe>(
      null!, _ => new Probe())).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddWhizbangWorkerAtSchemaReady<Probe>(null!))
      .Throws<ArgumentNullException>();
  }

  private static (StartupStageReporter Reporter, StartupStageGate Gate) _reporterAndGate(
      Whizbang.Core.Messaging.IWorkCoordinator? coordinator) {
    var services = new ServiceCollection();
    if (coordinator is not null) {
      services.AddScoped(_ => coordinator);
    }
    var gate = new StartupStageGate(SchemaReadyGate.AlreadyReady());
    var reporter = new StartupStageReporter(
      services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
      new Whizbang.Core.Observability.ServiceInstanceProvider(
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
      [gate],
      Microsoft.Extensions.Logging.Abstractions.NullLogger<StartupStageReporter>.Instance,
      new Whizbang.Core.Observability.LibraryVersionProvider("1.0.0"));
    return (reporter, gate);
  }

  /// <summary>A coordinator that keeps instance rows, and whose transition write fails until told otherwise.</summary>
  private sealed class FlakyRecorder : Whizbang.Core.Tests.Workers.NoOpWorkCoordinator, Whizbang.Core.Messaging.IWorkCoordinator {
    public bool Fails { get; set; }
    public int Written { get; private set; }
    public bool RecordsInstanceTransitions => true;

    public Task<bool> RecordInstanceTransitionAsync(InstanceTransition transition, CancellationToken cancellationToken = default) {
      if (Fails) {
        throw new InvalidOperationException("the database did not answer");
      }
      Written++;
      return Task.FromResult(true);
    }
  }

  /// <summary>Records each start and stop, and whether its start point had been reached when it was started.</summary>
  private sealed class Probe : IHostedService {
    public Func<bool> StartedWhen { get; init; } = static () => true;
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public bool StartPointReachedWhenStarted { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken) {
      Starts++;
      StartPointReachedWhenStarted = StartedWhen();
      return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
      Stops++;
      return Task.CompletedTask;
    }
  }

  /// <summary>Settles a startup signal as canceled when told its start was abandoned.</summary>
  private sealed class Abandonable : IHostedService, IStartAbandonedObserver {
    public TaskCompletionSource Abandoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Starts { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken) {
      Starts++;
      return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void OnStartAbandoned(CancellationToken stoppingToken) => Abandoned.TrySetCanceled(stoppingToken);
  }

  private sealed class Other : IHostedService {
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  }
}
