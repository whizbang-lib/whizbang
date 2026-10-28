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
/// A hosted service started at the startup stage it needs (#1636): the host starts the wrapper with everything else,
/// and the service itself starts only once this instance reaches its stage, so nothing it does touches the database
/// before then. Stopped before its stage, it is never started; stopped after, it is stopped; a failure of the service's
/// own loop is the wrapper's, so the host sees it as it would have unwrapped.
/// </summary>
/// <remarks>
/// Every wait is on a signal: the gate is opened by handing it the stage, the service records the gate's answer at the
/// moment it is started, and the wrapper's <c>Started</c> signal is what a test awaits.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStageGatedService.cs</code-under-test>
[Category("Startup")]
public class StartupStageGatedServiceTests {
  private static StartupStageSnapshot _at(StartupStage stage) => new(stage, "message", false, DateTimeOffset.UnixEpoch);

  [Test]
  [Timeout(30000)]
  public async Task TheServiceStarts_OnlyOnceItsStageIsReachedAsync(CancellationToken cancellationToken) {
    var gate = new StartupStageGate(new SchemaReadyGate());
    var inner = new RecordingService(gate, StartupStage.Coordinate);
    using var wrapper = new StartupStageGatedService<RecordingService>(inner, StartupStage.Coordinate, gate);

    await wrapper.StartAsync(cancellationToken);
    await gate.OnStageChangedAsync(_at(StartupStage.Migrate), true, cancellationToken);
    await gate.OnStageChangedAsync(_at(StartupStage.Coordinate), true, cancellationToken);
    await wrapper.Started.WaitAsync(cancellationToken);

    await Assert.That(inner.StageReachedWhenStarted).IsTrue()
      .Because("the service is started only after the gate says its stage has been reached");
    await Assert.That(inner.Starts).IsEqualTo(1);
    await Assert.That(wrapper.Inner).IsSameReferenceAs(inner);
    await Assert.That(wrapper.Stage).IsEqualTo(StartupStage.Coordinate);

    await wrapper.StopAsync(cancellationToken);
    await Assert.That(inner.Stops).IsEqualTo(1).Because("a started service is stopped with the host");
  }

  [Test]
  [Timeout(30000)]
  public async Task StoppedBeforeItsStage_TheServiceIsNeverStartedOrStoppedAsync(CancellationToken cancellationToken) {
    var gate = new StartupStageGate(new SchemaReadyGate());
    var inner = new RecordingService(gate, StartupStage.Coordinate);
    using var wrapper = new StartupStageGatedService<RecordingService>(inner, StartupStage.Coordinate, gate);
    await wrapper.StartAsync(cancellationToken);
    await gate.OnStageChangedAsync(_at(StartupStage.Migrate), true, cancellationToken);

    await wrapper.StopAsync(cancellationToken);

    await Assert.That(wrapper.ExecuteTask!.IsCompleted).IsTrue().Because("the stop ends the wait for the stage");
    await Assert.That(inner.Starts).IsEqualTo(0).Because("an instance that stops before Coordinate never coordinates");
    await Assert.That(inner.Stops).IsEqualTo(0);
    await Assert.That(wrapper.Started.IsCompleted).IsFalse();
  }

  [Test]
  [Timeout(30000)]
  public async Task WithNoGate_TheServiceStartsWithTheHostAsync(CancellationToken cancellationToken) {
    var inner = new RecordingService(null, StartupStage.Coordinate);
    using var wrapper = new StartupStageGatedService<RecordingService>(inner, StartupStage.Coordinate, gate: null);

    await wrapper.StartAsync(cancellationToken);
    await wrapper.Started.WaitAsync(cancellationToken);

    await Assert.That(inner.Starts).IsEqualTo(1);
    await wrapper.StopAsync(cancellationToken);
  }

  [Test]
  [Timeout(30000)]
  public async Task AFailureOfTheServicesOwnLoop_IsTheWrappersSoTheHostSeesItAsync(CancellationToken cancellationToken) {
    var gate = new StartupStageGate(new SchemaReadyGate());
    await gate.OnStageChangedAsync(_at(StartupStage.Coordinate), true, cancellationToken);
    var inner = new FailingLoop();
    using var wrapper = new StartupStageGatedService<FailingLoop>(inner, StartupStage.Coordinate, gate);

    await wrapper.StartAsync(cancellationToken);

    await Assert.That(async () => await wrapper.ExecuteTask!.WaitAsync(cancellationToken))
      .Throws<InvalidOperationException>().WithMessage("the loop failed");
  }

  [Test]
  [Timeout(30000)]
  public async Task AStoppedLoop_EndsTheWrapperWithoutAFaultAsync(CancellationToken cancellationToken) {
    var gate = new StartupStageGate(new SchemaReadyGate());
    await gate.OnStageChangedAsync(_at(StartupStage.Coordinate), true, cancellationToken);
    var inner = new WaitingLoop();
    using var wrapper = new StartupStageGatedService<WaitingLoop>(inner, StartupStage.Coordinate, gate);
    await wrapper.StartAsync(cancellationToken);
    await wrapper.Started.WaitAsync(cancellationToken);

    await wrapper.StopAsync(cancellationToken);

    await Assert.That(inner.ExecuteTask!.IsCompleted).IsTrue().Because("the service's own loop was stopped");
    await Assert.That(wrapper.ExecuteTask!.IsFaulted).IsFalse();
  }

  [Test]
  public async Task Registration_WrapsTheServiceAtItsStage_OncePerServiceTypeAsync() {
    var services = new ServiceCollection();
    var gate = new StartupStageGate(new SchemaReadyGate());
    services.AddSingleton<IStartupStageGate>(gate);
    services.AddSingleton(_ => new WaitingLoop());

    services.AddHostedServiceAtStage(StartupStage.Elect, sp => sp.GetRequiredService<WaitingLoop>());
    services.AddHostedServiceAtStage(StartupStage.Elect, sp => sp.GetRequiredService<WaitingLoop>());
    services.AddHostedServiceAtStage(StartupStage.Coordinate, _ => new RecordingService(null, StartupStage.Coordinate));
    await using var provider = services.BuildServiceProvider();

    var hosted = provider.GetServices<IHostedService>().ToList();
    await Assert.That(hosted.Count).IsEqualTo(2).Because("a second registration of the same service type changes nothing");
    var waiting = hosted.OfType<StartupStageGatedService<WaitingLoop>>().Single();
    await Assert.That(waiting.Stage).IsEqualTo(StartupStage.Elect);
    await Assert.That(waiting.Inner).IsSameReferenceAs(provider.GetRequiredService<WaitingLoop>());
    await Assert.That(hosted.OfType<StartupStageGatedService<RecordingService>>().Single().Stage).IsEqualTo(StartupStage.Coordinate);
  }

  [Test]
  public async Task NullArguments_AreRefusedAsync() {
    await Assert.That(() => new StartupStageGatedService<WaitingLoop>(null!, StartupStage.Elect, null))
      .Throws<ArgumentNullException>();
    await Assert.That(() => StartupStageGatedServiceCollectionExtensions.AddHostedServiceAtStage<WaitingLoop>(
      null!, StartupStage.Elect, _ => new WaitingLoop())).Throws<ArgumentNullException>();
    await Assert.That(() => new ServiceCollection().AddHostedServiceAtStage<WaitingLoop>(StartupStage.Elect, null!))
      .Throws<ArgumentNullException>();
  }

  /// <summary>Records each start and stop, and whether its stage had been reached when it was started.</summary>
  private sealed class RecordingService(IStartupStageGate? gate, StartupStage stage) : IHostedService {
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public bool StageReachedWhenStarted { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken) {
      Starts++;
      StageReachedWhenStarted = gate?.HasReached(stage) ?? true;
      return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
      Stops++;
      return Task.CompletedTask;
    }
  }

  private sealed class FailingLoop : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
      await Task.Yield();
      throw new InvalidOperationException("the loop failed");
    }
  }

  /// <summary>A loop that runs until it is stopped.</summary>
  private sealed class WaitingLoop : BackgroundService {
    private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _never.Task.WaitAsync(stoppingToken);
  }
}
