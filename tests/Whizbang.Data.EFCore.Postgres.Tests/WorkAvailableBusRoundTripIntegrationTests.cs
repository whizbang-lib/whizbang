// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the unify-now compatibility contract: when a SQL emitter calls
/// <c>notify_instance_owners('outbox' | 'inbox' | 'perspective', stream_ids)</c> — the exact payloads
/// today's <c>store_outbox_messages</c>, <c>store_inbox_messages</c>, and <c>_emit_event_store_chain</c>
/// SQL functions emit — a <see cref="SignalBus"/> subscriber to the corresponding typed signal
/// (<see cref="WorkOutboxAvailableSignal"/> etc.) MUST receive it via the Postgres transport with no
/// change to the SQL side. That is the whole point of the <see cref="WireNameAttribute"/> mapping:
/// the wire-name "outbox" round-trips to <see cref="WorkOutboxAvailableSignal"/> automatically.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[Category("Shard1")]
public class WorkAvailableBusRoundTripIntegrationTests : EFCoreTestBase {
  private async Task _pinStreamToInstanceAsync(Guid streamId, Guid instanceId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(@"
      INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, created_at, last_activity_at)
      VALUES (@stream_id, 0, @instance_id, NOW(), NOW())
      ON CONFLICT (stream_id) DO UPDATE
        SET assigned_instance_id = EXCLUDED.assigned_instance_id,
            last_activity_at = NOW();", conn);
    cmd.Parameters.AddWithValue("stream_id", streamId);
    cmd.Parameters.AddWithValue("instance_id", instanceId);
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _invokeNotifyInstanceOwnersAsync(string payload, Guid streamId) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT notify_instance_owners(@payload, @stream_ids)", conn);
    cmd.Parameters.AddWithValue(nameof(payload), payload);
    cmd.Parameters.Add(new NpgsqlParameter("stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = new[] { streamId },
    });
    await cmd.ExecuteNonQueryAsync();
    // 146 (#720): the doorbell was queued, not notified; the driver rings after the commit, modeled here.
    // Rings the way the driver rings: DoorbellRinger loops until a call rings nothing. A single raw
    // call rings one batch and stops, and since migration 206 a second ringer that finds the turn
    // held returns at once trusting the holder to drain, so a raw holder can strand a doorbell (#1344).
    _ = await DoorbellRinger.RingAsync(conn, DoorbellRinger.FUNCTION_NAME, null, CancellationToken.None);
  }

  [Test]
  [Timeout(120_000)]
  public async Task Outbox_SqlNotify_ReachesBusSubscriberAsWorkOutboxAvailableSignalAsync(CancellationToken cancellationToken) {
    var opts = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(cfg);
    using var shared = new PgSharedNotifyConnection(
      Options.Create(opts), cfg, instance,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);

    var available = _availabilityOf(shared);
    await ((IHostedService)shared).StartAsync(cancellationToken);
    await available.WaitAsync(cancellationToken);
    await Assert.That(shared.IsAvailable).IsTrue();

    var transport = new PostgresSignalTransport(
      Options.Create(opts), cfg, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    var bus = new SignalBus(transports: [transport], pullSources: []);

    var received = new TaskCompletionSource<WorkOutboxAvailableSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var sub = bus.Subscribe<WorkOutboxAvailableSignal>(s => { received.TrySetResult(s); return ValueTask.CompletedTask; });

    await bus.StartAsync(cancellationToken);

    // Deterministic completion signal: Subscribe only registers intent — the dispatch loop issues
    // the LISTEN asynchronously — so a NOTIFY emitted before then fires into a connection that is
    // not yet listening and is lost, pg_notify having no queue. Waiting for the channel to be
    // listened removes the race outright, rather than re-emitting until one attempt happens to win.
    await shared.WaitForChannelListenedAsync($"wh_work_i_{instance.InstanceId}", cancellationToken);

    // Pin a stream to this pod's instance and invoke notify_instance_owners — the exact call the
    // existing SQL emitters make today. If the wire-name / registry / transport chain is intact,
    // the bus subscriber receives WorkOutboxAvailableSignal without any SQL change on the emit side.
    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);

    await _invokeNotifyInstanceOwnersAsync("outbox", streamId);

    await received.Task.WaitAsync(cancellationToken);

    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(120_000)]
  public async Task Inbox_SqlNotify_ReachesBusSubscriberAsWorkInboxAvailableSignalAsync(CancellationToken cancellationToken) {
    var opts = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(cfg);
    using var shared = new PgSharedNotifyConnection(
      Options.Create(opts), cfg, instance,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);

    var available = _availabilityOf(shared);
    await ((IHostedService)shared).StartAsync(cancellationToken);
    await available.WaitAsync(cancellationToken);
    await Assert.That(shared.IsAvailable).IsTrue();

    var transport = new PostgresSignalTransport(
      Options.Create(opts), cfg, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    var bus = new SignalBus(transports: [transport], pullSources: []);

    var received = new TaskCompletionSource<WorkInboxAvailableSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var sub = bus.Subscribe<WorkInboxAvailableSignal>(s => { received.TrySetResult(s); return ValueTask.CompletedTask; });

    await bus.StartAsync(cancellationToken);

    // Deterministic completion signal: Subscribe only registers intent — the dispatch loop issues
    // the LISTEN asynchronously — so a NOTIFY emitted before then fires into a connection that is
    // not yet listening and is lost, pg_notify having no queue. Waiting for the channel to be
    // listened removes the race outright, rather than re-emitting until one attempt happens to win.
    await shared.WaitForChannelListenedAsync($"wh_work_i_{instance.InstanceId}", cancellationToken);

    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);

    await _invokeNotifyInstanceOwnersAsync("inbox", streamId);

    await received.Task.WaitAsync(cancellationToken);

    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }

  [Test]
  [Timeout(120_000)]
  public async Task Perspective_SqlNotify_ReachesBusSubscriberAsWorkPerspectiveAvailableSignalAsync(CancellationToken cancellationToken) {
    var opts = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(cfg);
    using var shared = new PgSharedNotifyConnection(
      Options.Create(opts), cfg, instance,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);

    var available = _availabilityOf(shared);
    await ((IHostedService)shared).StartAsync(cancellationToken);
    await available.WaitAsync(cancellationToken);
    await Assert.That(shared.IsAvailable).IsTrue();

    var transport = new PostgresSignalTransport(
      Options.Create(opts), cfg, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    var bus = new SignalBus(transports: [transport], pullSources: []);

    var received = new TaskCompletionSource<WorkPerspectiveAvailableSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var sub = bus.Subscribe<WorkPerspectiveAvailableSignal>(s => { received.TrySetResult(s); return ValueTask.CompletedTask; });

    await bus.StartAsync(cancellationToken);

    // Deterministic completion signal: Subscribe only registers intent — the dispatch loop issues
    // the LISTEN asynchronously — so a NOTIFY emitted before then fires into a connection that is
    // not yet listening and is lost, pg_notify having no queue. Waiting for the channel to be
    // listened removes the race outright, rather than re-emitting until one attempt happens to win.
    await shared.WaitForChannelListenedAsync($"wh_work_i_{instance.InstanceId}", cancellationToken);

    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);

    await _invokeNotifyInstanceOwnersAsync("perspective", streamId);

    await received.Task.WaitAsync(cancellationToken);

    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// The #505 regression lock: a DI-HOSTED bus — started by the host's IHostedService pipeline
  /// alone, with NO manual <see cref="SignalBus.StartAsync"/> anywhere — must deliver a SQL
  /// doorbell to a typed subscriber. Every sibling test above starts the bus by hand, which is
  /// exactly the neuter that let "the bus is registered but never started in production" stay
  /// invisible: transports never subscribed, every wire doorbell was dropped, and all work pumps
  /// ran at poll cadence. This test would have failed for as long as that gap existed.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task Perspective_SqlNotify_DiHostedBus_NoManualStart_ReachesSubscriberAsync(CancellationToken cancellationToken) {
    var opts = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(cfg);
    using var shared = new PgSharedNotifyConnection(
      Options.Create(opts), cfg, instance,
      new DiagLogger<PgSharedNotifyConnection>(_diag),
      connectionStringFallback: null,
      timeProvider: null);

    var available = _availabilityOf(shared);
    await ((IHostedService)shared).StartAsync(cancellationToken);
    await available.WaitAsync(cancellationToken);
    await Assert.That(shared.IsAvailable).IsTrue();

    var services = new ServiceCollection();
    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new DiagProvider(_diag)));
    services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(cfg);
    services.AddSingleton(Options.Create(opts));
    services.AddSingleton<IServiceInstanceProvider>(instance);
    services.AddSingleton<ISharedNotifyConnection>(shared);
    services.AddWhizbangSignalBus();
    // The first liveness probe is a real round trip through the database's LISTEN path; its
    // production window is 5 s, which a loaded test machine can miss while the signal itself still
    // arrives. The test asserts the probe's verdict, not its speed, so give it a wide window.
    services.Configure<Whizbang.Core.Signals.SignalBusOptions>(o => o.ProbeTimeoutMilliseconds = 30_000);
    // A slow probe, forced without a clock: the bus's first liveness probe reaches the wire only once the doorbell
    // has arrived. The probe's own window starts when it is published, and the test waits for the probe's verdict
    // itself, however late it comes, so ordering and machine load cannot fail it.
    var received = new TaskCompletionSource<WorkPerspectiveAvailableSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
    services.AddSingleton<PostgresSignalTransport>();
    services.AddSingleton<ISignalTransport>(sp => new DelayedProbeTransport(
      sp.GetRequiredService<PostgresSignalTransport>(), received.Task));
    await using var provider = services.BuildServiceProvider();

    var bus = provider.GetRequiredService<ISignalBus>();
    using var sub = bus.Subscribe<WorkPerspectiveAvailableSignal>(s => { received.TrySetResult(s); return ValueTask.CompletedTask; });

    // The HOST starts the bus — the seam under test. No SignalBus.StartAsync call anywhere here.
    foreach (var hosted in provider.GetServices<IHostedService>()) {
      await hosted.StartAsync(cancellationToken);
    }

    // Deterministic completion signal: Subscribe only registers intent — the dispatch loop issues
    // the LISTEN asynchronously — so a NOTIFY emitted before then fires into a connection that is
    // not yet listening and is lost, pg_notify having no queue. Waiting for the channel to be
    // listened removes the race outright, rather than re-emitting until one attempt happens to win.
    await shared.WaitForChannelListenedAsync($"wh_work_i_{instance.InstanceId}", cancellationToken);

    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);

    await _invokeNotifyInstanceOwnersAsync("perspective", streamId);

    _diag.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} TEST rang doorbell");
    try {
      await received.Task.WaitAsync(cancellationToken);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw new TimeoutException("signal not received; diagnostics:\n" + string.Join("\n", _diag));
    }

    // Bonus lock on the wire-route self-test: the hosted probe must have verified the REAL
    // Postgres transport end to end (pg_notify to own channel, back through typed dispatch).
    var liveness = provider.GetRequiredService<SignalBusLivenessState>();
    var probeVerdict = await liveness.FirstProbe.WaitAsync(cancellationToken);
    await Assert.That(probeVerdict).IsTrue();

    foreach (var hosted in provider.GetServices<IHostedService>()) {
      await hosted.StopAsync(CancellationToken.None);
    }
    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }


  /// <summary>
  /// The shared connection's first transition to available, observed from before it starts so the transition
  /// cannot be missed. The test waits for this transition rather than polling the flag against a clock.
  /// </summary>
  private static Task _availabilityOf(PgSharedNotifyConnection shared) {
    var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    shared.OnAvailabilityChanged += isAvailable => {
      if (isAvailable) {
        available.TrySetResult();
      }
    };
    return available.Task;
  }

  /// <summary>Holds the bus's liveness probe back from the wire until <paramref name="release"/> completes.</summary>
  private sealed class DelayedProbeTransport(ISignalTransport inner, Task release) : ISignalTransport {
    public Task StartAsync(ISignalSink sink, CancellationToken cancellationToken = default) =>
      inner.StartAsync(sink, cancellationToken);

    public async ValueTask PublishAsync<TSignal>(TSignal signal, SignalTarget target, CancellationToken cancellationToken = default)
        where TSignal : ISignal {
      if (signal is SignalBusProbeSignal) {
        await release.WaitAsync(cancellationToken);
      }
      await inner.PublishAsync(signal, target, cancellationToken);
    }
  }

  private readonly System.Collections.Concurrent.ConcurrentQueue<string> _diag = new();

  private sealed class DiagProvider(System.Collections.Concurrent.ConcurrentQueue<string> q) : ILoggerProvider {
    public ILogger CreateLogger(string categoryName) => new DiagLogger(q, categoryName);
    public void Dispose() { }
  }

  private class DiagLogger(System.Collections.Concurrent.ConcurrentQueue<string> q, string category) : ILogger {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      q.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel} {category}: {formatter(state, exception)} {exception?.GetType().Name}");
  }

  private sealed class DiagLogger<T>(System.Collections.Concurrent.ConcurrentQueue<string> q) : DiagLogger(q, typeof(T).Name), ILogger<T>;
}
