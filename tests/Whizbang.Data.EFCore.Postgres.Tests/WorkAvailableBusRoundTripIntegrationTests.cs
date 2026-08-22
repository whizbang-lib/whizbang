using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    cmd.Parameters.AddWithValue("payload", payload);
    cmd.Parameters.Add(new NpgsqlParameter("stream_ids", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid) {
      Value = new[] { streamId },
    });
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task Outbox_SqlNotify_ReachesBusSubscriberAsWorkOutboxAvailableSignalAsync() {
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

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await ((IHostedService)shared).StartAsync(cts.Token);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!shared.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, cts.Token);
    }
    await Assert.That(shared.IsAvailable).IsTrue();

    var transport = new PostgresSignalTransport(
      Options.Create(opts), cfg, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    var bus = new SignalBus([transport]);

    var received = new TaskCompletionSource<WorkOutboxAvailableSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var sub = bus.Subscribe<WorkOutboxAvailableSignal>(s => { received.TrySetResult(s); return ValueTask.CompletedTask; });

    await bus.StartAsync(cts.Token);

    // Pin a stream to this pod's instance and invoke notify_instance_owners — the exact call the
    // existing SQL emitters make today. If the wire-name / registry / transport chain is intact,
    // the bus subscriber receives WorkOutboxAvailableSignal without any SQL change on the emit side.
    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);

    await _notifyUntilReceivedAsync(
      () => _invokeNotifyInstanceOwnersAsync("outbox", streamId), received, cts.Token);

    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Inbox_SqlNotify_ReachesBusSubscriberAsWorkInboxAvailableSignalAsync() {
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

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await ((IHostedService)shared).StartAsync(cts.Token);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!shared.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, cts.Token);
    }
    await Assert.That(shared.IsAvailable).IsTrue();

    var transport = new PostgresSignalTransport(
      Options.Create(opts), cfg, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    var bus = new SignalBus([transport]);

    var received = new TaskCompletionSource<WorkInboxAvailableSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var sub = bus.Subscribe<WorkInboxAvailableSignal>(s => { received.TrySetResult(s); return ValueTask.CompletedTask; });

    await bus.StartAsync(cts.Token);
    await Task.Delay(200, cts.Token);

    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);
    await _invokeNotifyInstanceOwnersAsync("inbox", streamId);

    await received.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }

  [Test]
  public async Task Perspective_SqlNotify_ReachesBusSubscriberAsWorkPerspectiveAvailableSignalAsync() {
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

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await ((IHostedService)shared).StartAsync(cts.Token);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!shared.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, cts.Token);
    }
    await Assert.That(shared.IsAvailable).IsTrue();

    var transport = new PostgresSignalTransport(
      Options.Create(opts), cfg, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    var bus = new SignalBus([transport]);

    var received = new TaskCompletionSource<WorkPerspectiveAvailableSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var sub = bus.Subscribe<WorkPerspectiveAvailableSignal>(s => { received.TrySetResult(s); return ValueTask.CompletedTask; });

    await bus.StartAsync(cts.Token);
    await Task.Delay(200, cts.Token);

    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);
    await _invokeNotifyInstanceOwnersAsync("perspective", streamId);

    await received.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

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
  public async Task Perspective_SqlNotify_DiHostedBus_NoManualStart_ReachesSubscriberAsync() {
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

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await ((IHostedService)shared).StartAsync(cts.Token);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!shared.IsAvailable && DateTimeOffset.UtcNow < deadline) {
      await Task.Delay(50, cts.Token);
    }
    await Assert.That(shared.IsAvailable).IsTrue();

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(cfg);
    services.AddSingleton(Options.Create(opts));
    services.AddSingleton<IServiceInstanceProvider>(instance);
    services.AddSingleton<ISharedNotifyConnection>(shared);
    services.AddWhizbangSignalBus();
    services.AddSingleton<ISignalTransport, PostgresSignalTransport>();
    await using var provider = services.BuildServiceProvider();

    var bus = provider.GetRequiredService<ISignalBus>();
    var received = new TaskCompletionSource<WorkPerspectiveAvailableSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var sub = bus.Subscribe<WorkPerspectiveAvailableSignal>(s => { received.TrySetResult(s); return ValueTask.CompletedTask; });

    // The HOST starts the bus — the seam under test. No SignalBus.StartAsync call anywhere here.
    foreach (var hosted in provider.GetServices<IHostedService>()) {
      await hosted.StartAsync(cts.Token);
    }

    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);

    await _notifyUntilReceivedAsync(
      () => _invokeNotifyInstanceOwnersAsync("perspective", streamId), received, cts.Token);

    // Bonus lock on the wire-route self-test: the hosted probe must have verified the REAL
    // Postgres transport end to end (pg_notify to own channel, back through typed dispatch).
    var liveness = provider.GetRequiredService<SignalBusLivenessState>();
    var probeVerdict = await liveness.FirstProbe.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
    await Assert.That(probeVerdict).IsTrue();

    foreach (var hosted in provider.GetServices<IHostedService>()) {
      await hosted.StopAsync(CancellationToken.None);
    }
    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Re-invokes the SQL emitter until the bus subscriber reports the signal, or the deadline passes.
  /// </summary>
  /// <remarks>
  /// <c>pg_notify</c> reaches only sessions already LISTENing and is never queued, so a notification
  /// emitted before the LISTEN lands is lost permanently. Starting the bus (or the host) does not
  /// guarantee the LISTEN has been registered on the shared connection — that happens on the
  /// connection's own loop. A fixed <c>Task.Delay(200)</c> is a bet on that interval, and a loaded
  /// runner loses it: the notification is dropped and the test waits out its full timeout for a
  /// signal that no longer exists.
  ///
  /// <para>Re-emitting keeps the assertion intact — the contract is "notify_instance_owners reaches
  /// the typed subscriber", and if the chain is broken every attempt is dropped and the test still
  /// fails on the deadline.</para>
  /// </remarks>
  private static async Task _notifyUntilReceivedAsync<T>(
      Func<Task> emitAsync, TaskCompletionSource<T> received, CancellationToken cancellationToken,
      int timeoutSeconds = 15) {
    var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
    while (true) {
      await emitAsync();
      try {
        await received.Task.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
        return;
      } catch (TimeoutException) when (DateTimeOffset.UtcNow < deadline) {
        // The LISTEN had not landed when that notification went out — emit another.
      }
    }
  }
}
