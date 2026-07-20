using Microsoft.Extensions.Configuration;
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
    await Task.Delay(200, cts.Token);   // LISTEN resync

    // Pin a stream to this pod's instance and invoke notify_instance_owners — the exact call the
    // existing SQL emitters make today. If the wire-name / registry / transport chain is intact,
    // the bus subscriber receives WorkOutboxAvailableSignal without any SQL change on the emit side.
    var streamId = Guid.NewGuid();
    await _pinStreamToInstanceAsync(streamId, instance.InstanceId);
    await _invokeNotifyInstanceOwnersAsync("outbox", streamId);

    await received.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

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
}
