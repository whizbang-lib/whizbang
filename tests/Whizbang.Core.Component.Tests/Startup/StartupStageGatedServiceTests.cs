// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

  /// <summary>Records what is logged at Error or above.</summary>
  private sealed class ErrorLog : ILoggerProvider, ILogger {
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (IsEnabled(logLevel)) {
        _lines.Enqueue($"{logLevel} {formatter(state, exception)}");
      }
    }

    public void Dispose() { }
  }

  /// <summary>A hosted service whose start lasts until the host's start is canceled, as a long migration's would.</summary>
  private sealed class StartThatWaitsForCancellation : IHostedService {
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public async Task StartAsync(CancellationToken cancellationToken) {
      var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      await using var registration = cancellationToken.Register(() => canceled.TrySetCanceled(cancellationToken));
      _entered.TrySetResult();
      await canceled.Task;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  }

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

  /// <summary>
  /// A start canceled before the stage (a host's start aborted while startup is still running) cancels the wrapper's
  /// wait through the token the start was given, before the host is stopping. The wrapper ends without a fault or a
  /// cancellation: the host counts a canceled background service as a failed one unless it is already stopping, and from
  /// .NET 11 rethrows that failure from its stop (#2135).
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task AStartCanceledBeforeItsStage_EndsTheWrapperWithoutAFaultOrACancellationAsync(CancellationToken cancellationToken) {
    var gate = new StartupStageGate(new SchemaReadyGate());
    var inner = new RecordingService(gate, StartupStage.Coordinate);
    using var wrapper = new StartupStageGatedService<RecordingService>(inner, StartupStage.Coordinate, gate);
    using var start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    await wrapper.StartAsync(start.Token);

    await start.CancelAsync();
    await Task.WhenAny(wrapper.ExecuteTask!).WaitAsync(cancellationToken);

    await Assert.That(wrapper.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("a start that ends before the stage is the host stopping early, not the service failing");
    await Assert.That(inner.Starts).IsEqualTo(0);
    await wrapper.StopAsync(cancellationToken);
    await Assert.That(inner.Stops).IsEqualTo(0);
  }

  /// <summary>
  /// The same, through a host: its start is canceled while a gated service waits for its stage, and the host neither
  /// logs the wait as a failed service nor fails its stop. A host counts a canceled background service as a failed one
  /// unless it is already stopping, which a canceled start is not: it logs it at Error, and from .NET 11 also rethrows
  /// it from its stop (#2135).
  /// </summary>
  [Test]
  [Timeout(30000)]
  public async Task AHostWhoseStartIsCanceledBeforeTheStage_StopsWithoutFailingAsync(CancellationToken cancellationToken) {
    var errors = new ErrorLog();
    var gate = new StartupStageGate(new SchemaReadyGate());
    var inner = new RecordingService(gate, StartupStage.Coordinate);
    var holdsTheStart = new StartThatWaitsForCancellation();
    // Registered after the wrapper, so the wrapper is started and waiting while the host's start is still running.
    using var host = new HostBuilder()
      .ConfigureLogging(logging => logging.AddProvider(errors))
      .ConfigureServices(services => services
        .AddHostedService(_ => new StartupStageGatedService<RecordingService>(inner, StartupStage.Coordinate, gate))
        .AddHostedService(_ => holdsTheStart))
      .Build();
    var wrapper = host.Services.GetServices<IHostedService>().OfType<StartupStageGatedService<RecordingService>>().Single();
    using var start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var starting = host.StartAsync(start.Token);
    await holdsTheStart.Entered.WaitAsync(cancellationToken);

    await start.CancelAsync();
    await Task.WhenAny(starting).WaitAsync(cancellationToken);
    await Task.WhenAny(wrapper.ExecuteTask!).WaitAsync(cancellationToken);

    await Assert.That(async () => await host.StopAsync(cancellationToken)).ThrowsNothing()
      .Because("a host stopped before a service's stage stops cleanly");
    // The host logs its own canceled start ("Hosting failed to start"); what must not appear is a service's failure.
    await Assert.That(errors.Lines.Where(l => l.Contains("BackgroundService", StringComparison.Ordinal))).IsEmpty()
      .Because("a start canceled before the stage is no service's failure");
    await Assert.That(inner.Starts).IsEqualTo(0);
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
