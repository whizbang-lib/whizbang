// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// The durable-signal tail records each tick on the probe-cadence metrics when they are registered:
/// the idle series is the service's idle footprint, and a tail that never recorded would read as a
/// worker that never ran.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDurableSignalTailWorker.cs</code-under-test>
[Category("Integration")]
[Category("Shard5")]
public class PgDurableSignalTailWorkerBranchTests : EFCoreTestBase {

  [Test]
  [Timeout(60000)]
  public async Task Tick_WithProbeMetrics_RecordsAnIdleTickBeforeWaitingAsync(CancellationToken cancellationToken) {
    var probeMetrics = new ProbeCadenceMetrics(
      new WhizbangMetrics(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    var time = new WaitObservingTimeProvider();
    using var worker = new PgDurableSignalTailWorker(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = ConnectionString }),
      new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
      new ServiceInstanceProvider(Guid.NewGuid(), "tail-svc", "tail-host", processId: 1),
      new NoopSink(),
      NullLogger<PgDurableSignalTailWorker>.Instance,
      probeMetrics: probeMetrics,
      timeProvider: time);

    await worker.StartAsync(cancellationToken);
    try {
      // The first wait is created only after the first tick has been recorded.
      await time.FirstWaitCreated.WaitAsync(cancellationToken);
    } finally {
      await worker.StopAsync(CancellationToken.None);
    }

    await Assert.That(_tailTicks(probeMetrics, ProbeCadenceMetrics.OUTCOME_IDLE)).IsEqualTo(1L)
      .Because("no durable signal was waiting, so the one tick that ran is an idle one");
  }

  /// <summary>
  /// A dispatch still in flight when it returns is awaited before the tick moves on, so the cursor never
  /// advances past a signal whose delivery has not finished. The sink holds the delivery open on a gate the
  /// test controls: the tick is observed waiting on it, then completes once it opens.
  /// </summary>
  [Test]
  [Timeout(60000)]
  public async Task Tick_ADispatchStillInFlight_IsAwaitedBeforeTheTickCompletesAsync(CancellationToken cancellationToken) {
    const string wireName = "utest-tail-gated-2135";
    var instanceId = Guid.CreateVersion7();
    await using (var connection = new NpgsqlConnection(ConnectionString)) {
      await connection.OpenAsync(cancellationToken);
      await using var seed = new NpgsqlCommand(
        "INSERT INTO wh_signal_cursors (instance_id, last_delivered_signal_id) "
        + "VALUES (@instance_id, COALESCE((SELECT MAX(id) FROM wh_signals), 0));"
        + "INSERT INTO wh_signals (wire_name, target_instance_id) VALUES (@wire_name, NULL);", connection);
      seed.Parameters.AddWithValue("instance_id", instanceId);
      seed.Parameters.AddWithValue("wire_name", wireName);
      await seed.ExecuteNonQueryAsync(cancellationToken);
    }
    var options = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var sink = new GatedSink();
    using var worker = new PgDurableSignalTailWorker(
      Options.Create(options), configuration,
      new ServiceInstanceProvider(instanceId, "tail-svc", "tail-host", processId: 1),
      sink, NullLogger<PgDurableSignalTailWorker>.Instance);
    worker.UseWireMap(new Dictionary<string, SignalTypeEntry>(StringComparer.Ordinal) {
      [wireName] = new SignalTypeEntry(typeof(GatedProbe), wireName, SignalDeliveryClass.Durable, SignalTargeting.Broadcast,
        static (s, ct) => s.ReceiveAsync<GatedProbe>(default, ct)),
    });
    var plan = NotificationConnectionPlan.Create(null, NotificationConnectionStringResolver.Resolve(options, configuration));

    var tick = worker.TickOnceAsync(plan, cancellationToken);
    await sink.Entered.WaitAsync(cancellationToken);
    var completedWhileInFlight = tick.IsCompleted;
    sink.Release();
    var delivered = await tick;

    await Assert.That(completedWhileInFlight).IsFalse()
      .Because("the tick awaits a delivery that has not finished rather than moving on past it");
    await Assert.That(delivered).IsEqualTo(1);
    await Assert.That(sink.Completed).IsTrue();
  }

  private readonly record struct GatedProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  /// <summary>Holds each delivery open until the test releases it, and reports when one has started.</summary>
  private sealed class GatedSink : ISignalSink {
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;
    public bool Completed { get; private set; }

    public void Release() => _gate.TrySetResult();

    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
        where TSignal : ISignal {
      _entered.TrySetResult();
      return new ValueTask(_deliverAsync());
    }

    private async Task _deliverAsync() {
      await _gate.Task;
      Completed = true;
    }
  }

  private static long _tailTicks(ProbeCadenceMetrics metrics, string outcome) {
    long total = 0;
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, metrics.ProbeTicks.Instrument)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      var isTail = false;
      var isOutcome = false;
      foreach (var tag in tags) {
        isTail |= tag.Key == ProbeCadenceMetrics.PROBE_TAG && Equals(tag.Value, ProbeCadenceMetrics.PROBE_DURABLE_SIGNAL_TAIL);
        isOutcome |= tag.Key == ProbeCadenceMetrics.OUTCOME_TAG && Equals(tag.Value, outcome);
      }
      if (isTail && isOutcome) {
        total += value;
      }
    });
    listener.Start();
    listener.RecordObservableInstruments();
    return total;
  }

  /// <summary>
  /// Reports the first timer the worker creates (its first inter-tick wait) and never fires it, so
  /// the worker parks there until it is stopped.
  /// </summary>
  private sealed class WaitObservingTimeProvider : TimeProvider {
    private readonly TaskCompletionSource _firstWait = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task FirstWaitCreated => _firstWait.Task;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      _firstWait.TrySetResult();
      return TimeProvider.System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
  }

  private sealed class NoopSink : ISignalSink {
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
        where TSignal : ISignal => ValueTask.CompletedTask;
  }
}
