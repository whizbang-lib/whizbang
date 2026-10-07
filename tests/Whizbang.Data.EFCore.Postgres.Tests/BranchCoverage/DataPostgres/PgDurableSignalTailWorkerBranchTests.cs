// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
