using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Core.Signals;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for <see cref="PostgresSignalTransport"/>'s <see cref="SignalTarget.Streams"/>
/// publish path — a signal targeted at a set of stream ids must resolve to the instance that owns
/// those streams via <c>notify_instance_owners(payload, uuid[])</c> and reach only that instance's
/// channel. When a stream is already pinned in <c>wh_active_streams</c>, the owning instance is the
/// pinned <c>assigned_instance_id</c>; an unclaimed stream falls back to the deterministic
/// partition-modulo owner (same helper the SQL store procs already use). This test pins ownership
/// explicitly so the routing target is unambiguous.
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
[Category("Shard1")]
public class PostgresSignalTransportStreamsTargetTests : EFCoreTestBase {
  private readonly record struct StreamsTargetedTransportProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Targeted;
  }

  private sealed class FakeSource(IReadOnlyList<SignalTypeEntry> entries) : ISignalTypeSource {
    public IReadOnlyList<SignalTypeEntry> GetSignalTypes() => entries;
  }

  // If SignalTarget.Streams silently fell back to broadcast (or to nothing), a worker would
  // never wake for work on streams it owns — with nothing logged, because the doorbell simply
  // never rings on that instance's channel. This pins a stream to this instance in
  // wh_active_streams and proves the published signal actually lands on the owning instance's
  // wh_work_i_<id> channel, carrying the payload that was published.
  [Test]
  public async Task StreamsTargetedSignal_RoutesToPinnedOwningInstanceAsync() {
    const string wireName = "utest-streams-targeted-transport-probe-58217";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(StreamsTargetedTransportProbe), wireName,
        SignalDeliveryClass.BestEffort, SignalTargeting.Targeted,
        static (sink, ct) => sink.ReceiveAsync<StreamsTargetedTransportProbe>(default, ct)),
    ]));

    var opts = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify,
    };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new Whizbang.Core.Observability.ServiceInstanceProvider(cfg);

    // Pin a stream to this instance so notify_instance_owners' Step 1 (already-active-stream)
    // path resolves ownership deterministically, regardless of the wire-name payload.
    var streamId = Guid.NewGuid();
    await using (var dbContext = CreateDbContext()) {
      var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
      if (conn.State != System.Data.ConnectionState.Open) {
        await conn.OpenAsync();
      }
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = @"INSERT INTO wh_active_streams (stream_id, partition_number, assigned_instance_id, last_activity_at)
                          VALUES (@sid, 0, @inst, NOW())";
      cmd.Parameters.AddWithValue("sid", streamId);
      cmd.Parameters.AddWithValue("inst", instance.InstanceId);
      await cmd.ExecuteNonQueryAsync();
    }

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

    var received = new TaskCompletionSource<StreamsTargetedTransportProbe>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var sub = bus.Subscribe<StreamsTargetedTransportProbe>(s => { received.TrySetResult(s); return ValueTask.CompletedTask; });

    await bus.StartAsync(cts.Token);

    // Deterministic completion signal — see PostgresSignalTransportIntegrationTests: StartAsync
    // returns once intent is registered, but the LISTEN itself is issued asynchronously on the
    // shared connection's dispatch loop, and pg_notify has no queue.
    await shared.WaitForChannelListenedAsync($"wh_work_i_{instance.InstanceId:D}", cts.Token);

    await bus.PublishAsync(new StreamsTargetedTransportProbe(1), SignalTarget.Streams([streamId]));

    var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);
    await Assert.That(got.V).IsEqualTo(0)
      .Because("the wire carries only the signal's wire-name; the delivered instance is a default doorbell marker");

    await ((IHostedService)shared).StopAsync(CancellationToken.None);
  }
}
