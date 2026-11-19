// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Core.Tests.Notifications;

/// <summary>
/// The Pg notification stack's table-touching workers keep running through a database outage. When
/// they start is a fact of their registration (#1636): the host starts each at its start point, so
/// none of them waits on the schema-ready gate itself.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgInstanceLifecycleMonitor.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgDurableSignalRetentionWorker.cs</code-under-test>
/// <remarks>
/// A host that names no database reaches the "nothing to connect to" side of each worker: the monitor's tick and the
/// retention sweep both do nothing and say so, rather than throwing on a connection that cannot be opened.
/// </remarks>
[Category("Startup")]
[NotInParallel(Order = 103)]
public class PgNotificationStackStartupGateTests {

  private static IConfiguration _plainConfig() =>
    new ConfigurationBuilder().AddInMemoryCollection([]).Build();

  [Test]
  [Timeout(60000)]
  public async Task InstanceLifecycleMonitor_AnUnreachableDatabase_DoesNotStopDeathDetectionAsync(
      CancellationToken ct) {
    // This loop is what announces a dead pod, and the announcement is what triggers takeover of
    // the streams that pod owned. If a failed tick ended the loop, no death would be announced
    // again for the life of the process and those streams would stay stranded -- with nothing in
    // the logs after the first warning to say the monitor had stopped watching.
    var log = new EventCountingLogger<PgInstanceLifecycleMonitor>(TICK_FAILED_EVENT_ID, target: 2);
    var worker = new PgInstanceLifecycleMonitor(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = UNREACHABLE_DATABASE }),
      _plainConfig(),
      new NoOpSignalBus(),
      log);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    // The SECOND failure is the assertion: it can only happen if the loop survived the first and
    // came back round after its interval. The first proves only that a tick ran.
    await log.Reached.WaitAsync(ct);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the monitor keeps scanning through a database outage and still stops cleanly "
             + "when asked");
  }

  /// <summary>A refused port: the connection plan is available, and opening it always fails.</summary>
  private const string UNREACHABLE_DATABASE =
    "Host=127.0.0.1;Port=1;Database=none;Username=u;Password=p;Timeout=1;Command Timeout=1";

  [Test]
  [Timeout(60000)]
  public async Task DurableSignalRetention_AnUnreachableDatabase_DoesNotStopTheSweepAsync(
      CancellationToken ct) {
    // The sweep is the only thing bounding wh_signals. Every durable signal ever published stays
    // in that table until this loop deletes it, so a loop that ends on one failed sweep leaves the
    // table growing without limit for the life of the process -- and the next sweep after a
    // restart has that much more to delete.
    var log = new EventCountingLogger<PgDurableSignalRetentionWorker>(SWEEP_FAILED_EVENT_ID, target: 2);
    var worker = new PgDurableSignalRetentionWorker(
      Options.Create(new WhizbangNotificationOptions { DirectConnectionString = UNREACHABLE_DATABASE }),
      _plainConfig(),
      log) {
      SweepInterval = TimeSpan.FromMilliseconds(50),
    };

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await log.Reached.WaitAsync(ct);
    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(worker.ExecuteTask!.Status).IsEqualTo(TaskStatus.RanToCompletion)
      .Because("the sweep retries through an outage and still stops cleanly when asked");
  }

  /// <summary>
  /// A host whose notification options name no database (no connection string, no data source) has
  /// nothing to scan: the tick reads nothing, announces no death, and comes back at the cadence of a
  /// fleet with no stale peer, rather than throwing on a connection it cannot open.
  /// </summary>
  [Test]
  public async Task InstanceLifecycleMonitor_WithNoDatabaseToReach_ScansNothingAndKeepsItsIdleCadenceAsync(
      CancellationToken ct) {
    var bus = new NoOpSignalBus();
    var worker = new PgInstanceLifecycleMonitor(
      Options.Create(new WhizbangNotificationOptions()),
      _plainConfig(),
      bus,
      Microsoft.Extensions.Logging.Abstractions.NullLogger<PgInstanceLifecycleMonitor>.Instance);

    var next = await worker.TickForTestsAsync(ct);

    await Assert.That(next).IsEqualTo(PgInstanceLifecycleMonitor.ComputeTickInterval(null, worker.StaleThreshold))
      .Because("with nothing to read there is no oldest stale peer, so the tick keeps the idle cadence");
    await Assert.That(bus.Published).IsEqualTo(0)
      .Because("a monitor that cannot see the fleet table must never announce a death");
  }

  /// <summary>
  /// A host whose notification options name no database has no signal table to sweep: the sweep deletes
  /// nothing and reports zero rather than throwing on a connection it cannot open.
  /// </summary>
  [Test]
  public async Task DurableSignalRetention_WithNoDatabaseToReach_SweepsNothingAsync(CancellationToken ct) {
    var worker = new PgDurableSignalRetentionWorker(
      Options.Create(new WhizbangNotificationOptions()),
      _plainConfig(),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<PgDurableSignalRetentionWorker>.Instance);

    await Assert.That(await worker.SweepOnceAsync(ct)).IsEqualTo(0);
  }

  private const int TICK_FAILED_EVENT_ID = 1;
  private const int SWEEP_FAILED_EVENT_ID = 1;

  /// <summary>
  /// Completes a task once a given log event has been seen <c>target</c> times.
  /// </summary>
  /// <remarks>
  /// The second occurrence is what these tests wait on. The first only proves a pass ran; the
  /// second cannot happen unless the loop survived that pass and came back round after its
  /// interval, which is the whole claim.
  /// </remarks>
  private sealed class EventCountingLogger<T>(int expectedEventId, int target) : ILogger<T> {
    private readonly TaskCompletionSource _reached =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _seen;

    public Task Reached => _reached.Task;

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      if (eventId.Id == expectedEventId && Interlocked.Increment(ref _seen) >= target) {
        _reached.TrySetResult();
      }
    }
  }

  private sealed class NoOpSignalBus : ISignalBus {
    private int _published;

    public int Published => Volatile.Read(ref _published);

    private sealed class Subscription : ISignalSubscription {
      public void Dispose() { }
    }
    public ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target = default, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Interlocked.Increment(ref _published);
      return ValueTask.CompletedTask;
    }
    public ISignalSubscription Subscribe<TSignal>(Func<TSignal, ValueTask> handler)
      where TSignal : ISignal => new Subscription();
  }

}
