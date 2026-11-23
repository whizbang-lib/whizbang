// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Tests.Workers;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The held-age reading (#2542): each instance measures how long the oldest outbox event it holds for its commit-order
/// stamp has waited, publishes it as a gauge, and warns once it passes the configured age. A hold until stamped can wait
/// hours behind a stamp backlog, and this is how an operator sees it.
/// </summary>
/// <remarks>
/// Every wait is on a signal: the worker arming its timer on the fake clock, which the test then advances, and the
/// sample's completion. Nothing sleeps.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Observability/SequenceHoldWorker.cs</code-under-test>
/// <docs>fundamentals/messaging/sequence-integrity#watching-held-messages</docs>
[Category("Core")]
[Category("Observability")]
public class SequenceHoldWorkerTests {
  private const string GAUGE = "whizbang.outbox.sequence_hold.oldest_age_seconds";
  private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);

  [Test]
  public async Task OnItsInterval_ReadsThisInstancesOldestHold_AndPublishesItsAgeAsync() {
    await using var host = new WorkerHost(new WhizbangCoreOptions { SequenceHoldSampleInterval = TimeSpan.FromSeconds(15) });
    host.Coordinator.Oldest = host.Clock.GetUtcNow().AddSeconds(-20);

    var sampled = host.NextSample();
    await host.StartAsync();
    await host.Clock.WaitForArmedAsync(TimeSpan.FromSeconds(15), _wait);
    host.Clock.Advance(TimeSpan.FromSeconds(15));
    var age = await sampled.WaitAsync(_wait);

    await Assert.That(age).IsEqualTo(TimeSpan.FromSeconds(35));
    await Assert.That(host.Coordinator.AskedFor).Contains((host.InstanceId, SequenceHoldWorker.MAX_ROWS))
      .Because("each instance reports the holds it owns, through a reading bounded by the batch it walks");
    await Assert.That(host.Read()).IsEquivalentTo([35d]);
    await Assert.That(host.Logger.Collector.GetSnapshot().Any(r => r.Level == LogLevel.Warning)).IsFalse()
      .Because("35 seconds is under the default one-minute warning age");
  }

  [Test]
  public async Task AHoldOlderThanTheWarningAge_IsLoggedAsAWarningAsync() {
    await using var host = new WorkerHost(new WhizbangCoreOptions { SequenceHoldWarningAge = TimeSpan.FromMinutes(5) });
    host.Coordinator.Oldest = host.Clock.GetUtcNow().AddMinutes(-6);

    var age = await host.Worker.SampleOnceAsync(CancellationToken.None);

    await Assert.That(age).IsEqualTo(TimeSpan.FromMinutes(6));
    var warning = host.Logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Warning);
    await Assert.That(warning.Message).Contains("for its commit-order stamp");
  }

  [Test]
  public async Task NothingHeld_ReadsZero_AndAFailedReading_ReportsNothingAsync() {
    await using var host = new WorkerHost(new WhizbangCoreOptions());

    await Assert.That(await host.Worker.SampleOnceAsync(CancellationToken.None)).IsEqualTo(TimeSpan.Zero);
    await Assert.That(host.Read()).IsEquivalentTo([0d])
      .Because("nothing held is a reading of zero, so a dashboard shows a flat line, not a gap");

    host.Coordinator.Failure = new InvalidOperationException("the database is unreachable");
    await Assert.That(await host.Worker.SampleOnceAsync(CancellationToken.None)).IsNull();
    await Assert.That(host.Read()).IsEmpty()
      .Because("an unknown age is not reported as a young one");
    var failure = host.Logger.Collector.GetSnapshot().Single(r => r.Level == LogLevel.Warning);
    await Assert.That(failure.Exception).IsTypeOf<InvalidOperationException>();
  }

  [Test]
  public async Task AnEventStampedAfterTheReadingStarted_NeverReadsAsANegativeAgeAsync() {
    await using var host = new WorkerHost(new WhizbangCoreOptions());
    host.Coordinator.Oldest = host.Clock.GetUtcNow().AddSeconds(5);

    await Assert.That(await host.Worker.SampleOnceAsync(CancellationToken.None)).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task WithNoWorkCoordinator_ReportsNothingAsync() {
    await using var host = new WorkerHost(new WhizbangCoreOptions(), withCoordinator: false);

    await Assert.That(await host.Worker.SampleOnceAsync(CancellationToken.None)).IsNull();
    await Assert.That(host.Read()).IsEmpty();
  }

  [Test]
  public async Task AnIntervalOfZero_TurnsTheReadingOffAsync() {
    await using var host = new WorkerHost(new WhizbangCoreOptions { SequenceHoldSampleInterval = TimeSpan.Zero });

    await host.StartAsync();
    await host.Worker.ExecuteTask!.WaitAsync(_wait);

    await Assert.That(host.Coordinator.AskedFor).IsEmpty();
    await Assert.That(host.Worker.DbMechanism).IsEqualTo(DbMechanism.Outbox);
  }

  [Test]
  public async Task Stopping_EndsTheLoopWhileItWaitsAsync() {
    await using var host = new WorkerHost(new WhizbangCoreOptions());

    await host.StartAsync();
    await host.Clock.WaitForArmedAsync(TimeSpan.FromSeconds(15), _wait);
    await host.StopAsync();

    await Assert.That(host.Worker.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
    await Assert.That(host.Coordinator.AskedFor).IsEmpty();
  }

  [Test]
  public async Task ACancellationThatIsNotOurs_IsAnUnknownAge_AndOursIsLetThroughAsync() {
    await using var host = new WorkerHost(new WhizbangCoreOptions());
    host.Coordinator.Failure = new OperationCanceledException("a command timeout");

    await Assert.That(await host.Worker.SampleOnceAsync(CancellationToken.None)).IsNull()
      .Because("a cancellation the worker did not ask for is a failed reading, not a shutdown");

    using var canceled = new CancellationTokenSource();
    await canceled.CancelAsync();
    await Assert.That(async () => await host.Worker.SampleOnceAsync(canceled.Token)).Throws<OperationCanceledException>();
  }

  [Test]
  public async Task StoppingWhileAReadingRuns_EndsTheLoopWhenTheReadingReturnsAsync() {
    await using var host = new WorkerHost(new WhizbangCoreOptions());
    host.Coordinator.OnRead = host.CancelStopping;
    host.Coordinator.Oldest = host.Clock.GetUtcNow();

    await host.StartAsync();
    await host.Clock.WaitForArmedAsync(TimeSpan.FromSeconds(15), _wait);
    host.Clock.Advance(TimeSpan.FromSeconds(15));
    await host.Worker.ExecuteTask!.WaitAsync(_wait);

    await Assert.That(host.Coordinator.AskedFor).Count().IsEqualTo(1)
      .Because("the reading in flight finishes, and the loop ends before waiting again");
  }

  [Test]
  public async Task AReadingWithNoListener_StillPublishesAsync() {
    using var factory = new TestMeterFactory();
    var coordinator = new HeldAgeCoordinator();
    await using var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(coordinator).BuildServiceProvider();
    using var worker = new SequenceHoldWorker(
      services.GetRequiredService<IServiceScopeFactory>(), new FixedInstance(Guid.CreateVersion7()), new WhizbangCoreOptions(),
      new SequenceHoldMetrics(new WhizbangMetrics(factory)), new FakeLogger<SequenceHoldWorker>());

    await Assert.That(await worker.SampleOnceAsync(CancellationToken.None)).IsEqualTo(TimeSpan.Zero);
  }

  [Test]
  public async Task MissingDependencies_AreRefusedAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new SequenceHoldMetrics(new WhizbangMetrics(factory));
    var services = new ServiceCollection().BuildServiceProvider();
    var scopes = services.GetRequiredService<IServiceScopeFactory>();
    var instance = new FixedInstance(Guid.CreateVersion7());
    var logger = new FakeLogger<SequenceHoldWorker>();
    var options = new WhizbangCoreOptions();

    await Assert.That(() => new SequenceHoldWorker(null!, instance, options, metrics, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new SequenceHoldWorker(scopes, null!, options, metrics, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new SequenceHoldWorker(scopes, instance, null!, metrics, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new SequenceHoldWorker(scopes, instance, options, null!, logger)).Throws<ArgumentNullException>();
    await Assert.That(() => new SequenceHoldWorker(scopes, instance, options, metrics, null!)).Throws<ArgumentNullException>();
    await Assert.That(() => new SequenceHoldMetrics(null!)).Throws<ArgumentNullException>();
  }

  /// <summary>A coordinator that answers the held-age reading and records each request.</summary>
  private sealed class HeldAgeCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public DateTimeOffset? Oldest { get; set; }
    public Exception? Failure { get; set; }
    public List<(Guid InstanceId, int MaxRows)> AskedFor { get; } = [];
    public Action? OnRead { get; set; }

    public Task<DateTimeOffset?> GetOldestSequenceHeldOutboxAsync(Guid instanceId, int maxRows, CancellationToken cancellationToken = default) {
      lock (AskedFor) {
        AskedFor.Add((instanceId, maxRows));
      }
      OnRead?.Invoke();
      return Failure is { } failure ? Task.FromException<DateTimeOffset?>(failure) : Task.FromResult(Oldest);
    }
  }

  private sealed class FixedInstance(Guid instanceId) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = instanceId;
    public string ServiceName => "test-svc";
    public string HostName => "test-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() { InstanceId = InstanceId, ServiceName = ServiceName, HostName = HostName, ProcessId = ProcessId };
  }

  /// <summary>The worker on a fake clock, with a listener on its gauge.</summary>
  private sealed class WorkerHost : IAsyncDisposable {
    private readonly ServiceProvider _container;
    private readonly TestMeterFactory _factory = new();
    private readonly MeterListener _listener = new();
    private readonly List<double> _readings = [];
    private readonly CancellationTokenSource _stopping = new();
    private TaskCompletionSource<TimeSpan?> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorkerHost(WhizbangCoreOptions options, bool withCoordinator = true) {
      var services = new ServiceCollection();
      if (withCoordinator) {
        services.AddSingleton<IWorkCoordinator>(Coordinator);
      }
      _container = services.BuildServiceProvider();
      Worker = new SequenceHoldWorker(
        _container.GetRequiredService<IServiceScopeFactory>(),
        new FixedInstance(InstanceId),
        options,
        new SequenceHoldMetrics(new WhizbangMetrics(_factory)),
        Logger,
        Clock);
      Worker.Sampled += age => Volatile.Read(ref _next).TrySetResult(age);

      var meter = _factory.CreatedMeters[0];
      _listener.InstrumentPublished = (instrument, listener) => {
        if (instrument.Meter == meter && instrument.Name == GAUGE) {
          listener.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<double>((_, value, _, _) => _readings.Add(value));
      _listener.Start();
    }

    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public HeldAgeCoordinator Coordinator { get; } = new();
    public FakeLogger<SequenceHoldWorker> Logger { get; } = new();
    public ArmedTimerClock Clock { get; } = new();
    public SequenceHoldWorker Worker { get; }

    public Task<TimeSpan?> NextSample() {
      var next = new TaskCompletionSource<TimeSpan?>(TaskCreationOptions.RunContinuationsAsynchronously);
      Volatile.Write(ref _next, next);
      return next.Task;
    }

    public Task StartAsync() => Worker.StartAsync(_stopping.Token);

    public void CancelStopping() => _stopping.Cancel();

    public async Task StopAsync() {
      await _stopping.CancelAsync();
      await Worker.StopAsync(CancellationToken.None);
    }

    public List<double> Read() {
      _readings.Clear();
      _listener.RecordObservableInstruments();
      return [.. _readings];
    }

    public async ValueTask DisposeAsync() {
      await StopAsync();
      Worker.Dispose();
      _listener.Dispose();
      _factory.Dispose();
      _stopping.Dispose();
      await _container.DisposeAsync();
    }
  }
}
