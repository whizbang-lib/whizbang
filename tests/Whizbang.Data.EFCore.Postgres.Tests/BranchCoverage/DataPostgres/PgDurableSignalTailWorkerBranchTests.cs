// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Threading.Tasks.Sources;
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
  /// advances past a signal whose delivery has not finished. The sink hands back a delivery the test
  /// controls and reports the moment the tick awaits it; the delivery is finished only after that, so the
  /// tick cannot find it already complete and step past the await.
  /// </summary>
  /// <remarks>
  /// Waiting only for the delivery to start is not enough (#2405): the tick still has to return from the
  /// sink and test the delivery before it awaits, and a delivery finished inside that window let the tick
  /// skip the await while every assertion still passed.
  /// </remarks>
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
    // The delivery cannot finish until it is released, so the tick either awaits it or moves on without it,
    // and whichever happens first is the answer: a tick that moved on completes while the delivery is open.
    var first = await Task.WhenAny(sink.Awaited, tick).WaitAsync(cancellationToken);
    var awaitedWhileInFlight = first == sink.Awaited;
    sink.Release();
    var delivered = await tick;

    await Assert.That(awaitedWhileInFlight).IsTrue()
      .Because("the tick awaits a delivery that has not finished rather than moving on past it");
    await Assert.That(delivered).IsEqualTo(1);
    await Assert.That(sink.ResultTaken).IsTrue()
      .Because("the tick resumes from the awaited delivery and takes its outcome");
  }

  private readonly record struct GatedProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  /// <summary>
  /// Hands back one delivery that stays open until the test releases it, and reports when the caller awaits it.
  /// Awaiting a pending <see cref="ValueTask"/> registers a continuation with its source, which is the one point
  /// that observes the await itself rather than the start of the delivery.
  /// </summary>
  private sealed class GatedSink : ISignalSink, IValueTaskSource {
    private readonly TaskCompletionSource _awaited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ManualResetValueTaskSourceCore<bool> _delivery = new() { RunContinuationsAsynchronously = true };
    private int _resultTaken;

    /// <summary>Completes when the caller awaits the delivery while it is still open.</summary>
    public Task Awaited => _awaited.Task;
    public bool ResultTaken => Volatile.Read(ref _resultTaken) == 1;

    public void Release() => _delivery.SetResult(true);

    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
        where TSignal : ISignal => new(this, _delivery.Version);

    public ValueTaskSourceStatus GetStatus(short token) => _delivery.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) {
      _delivery.OnCompleted(continuation, state, token, flags);
      _awaited.TrySetResult();
    }

    public void GetResult(short token) {
      _delivery.GetResult(token);
      Volatile.Write(ref _resultTaken, 1);
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
