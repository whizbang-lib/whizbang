using Microsoft.Extensions.Configuration;
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
/// Integration tests for the durable-signal path — signals declared as
/// <see cref="SignalDeliveryClass.Durable"/> must persist to <c>wh_signals</c> on publish and
/// be delivered by <see cref="PgDurableSignalTailWorker"/> even without a NOTIFY. Best-effort
/// signals must NOT persist (the fast path is NOTIFY only).
/// </summary>
/// <docs>fundamentals/signal-bus/signal-bus</docs>
public class PgDurableSignalTailIntegrationTests : EFCoreTestBase {
  private readonly record struct DurableProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.Durable;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  private sealed class FakeSource(IReadOnlyList<SignalTypeEntry> entries) : ISignalTypeSource {
    public IReadOnlyList<SignalTypeEntry> GetSignalTypes() => entries;
  }

  private sealed class CountingSink : ISignalSink {
    public int Received { get; private set; }
    public ValueTask ReceiveAsync<TSignal>(TSignal signal, CancellationToken cancellationToken = default)
      where TSignal : ISignal {
      Received++;
      return ValueTask.CompletedTask;
    }
  }

  private (PostgresSignalTransport Transport, IServiceInstanceProvider Instance) _createTransport(Guid instanceId) {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(instanceId, "utest-svc", "utest-host", processId: 1);
    // The transport needs a shared connection for LISTEN but our durable test does not exercise
    // the LISTEN loop — only PublishAsync's INSERT path. Use a real PgSharedNotifyConnection
    // to satisfy the ctor; StartAsync is optional for publish-only usage.
    var shared = new PgSharedNotifyConnection(
      Options.Create(opts), cfg, instance,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);
    var transport = new PostgresSignalTransport(
      Options.Create(opts), cfg, shared, instance, NullLogger<PostgresSignalTransport>.Instance);
    return (transport, instance);
  }

  private PgDurableSignalTailWorker _createTail(Guid instanceId, ISignalSink sink) {
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instance = new ServiceInstanceProvider(instanceId, "utest-svc", "utest-host", processId: 1);
    return new PgDurableSignalTailWorker(
      Options.Create(opts), cfg, instance, sink,
      NullLogger<PgDurableSignalTailWorker>.Instance);
  }

  private async Task<long> _selectMaxSignalIdAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand("SELECT COALESCE(MAX(id), 0) FROM wh_signals", conn);
    return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0, System.Globalization.CultureInfo.InvariantCulture);
  }

  [Test]
  public async Task DurableSignal_Publish_AppendsRowToWhSignalsAsync() {
    const string wireName = "utest-durable-append-33711";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(DurableProbe), wireName,
        SignalDeliveryClass.Durable, SignalTargeting.Broadcast,
        static (sink, ct) => sink.ReceiveAsync<DurableProbe>(default, ct)),
    ]));

    var (transport, _) = _createTransport(Guid.NewGuid());
    await transport.StartAsync(new CountingSink());

    var before = await _selectMaxSignalIdAsync();
    await transport.PublishAsync(new DurableProbe(1), SignalTarget.Broadcast);
    var after = await _selectMaxSignalIdAsync();

    await Assert.That(after).IsGreaterThan(before)
      .Because("a Durable signal must be persisted to wh_signals in addition to NOTIFY");
  }

  private readonly record struct BestEffortProbe(int V) : ISignal {
    public static SignalDeliveryClass DeliveryClass => SignalDeliveryClass.BestEffort;
    public static SignalTargeting Targeting => SignalTargeting.Broadcast;
  }

  [Test]
  public async Task BestEffortSignal_Publish_DoesNotAppendToWhSignalsAsync() {
    const string wireName = "utest-besteffort-noappend-98211";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(BestEffortProbe), wireName,
        SignalDeliveryClass.BestEffort, SignalTargeting.Broadcast,
        static (sink, ct) => sink.ReceiveAsync<BestEffortProbe>(default, ct)),
    ]));

    var (transport, _) = _createTransport(Guid.NewGuid());
    await transport.StartAsync(new CountingSink());

    var before = await _selectMaxSignalIdAsync();
    await transport.PublishAsync(new BestEffortProbe(1), SignalTarget.Broadcast);
    var after = await _selectMaxSignalIdAsync();

    await Assert.That(after).IsEqualTo(before)
      .Because("best-effort signals must NOT persist to wh_signals — the fast path is NOTIFY only");
  }

  [Test]
  public async Task DurableSignal_PersistedBeforeTailStart_DeliveredOnNextTickAsync() {
    // Register a unique durable type so the tail's dispatch dictionary picks it up.
    const string wireName = "utest-durable-tail-42781";
    SignalTypeRegistry.Register(new FakeSource([
      new SignalTypeEntry(typeof(DurableProbe), wireName,
        SignalDeliveryClass.Durable, SignalTargeting.Broadcast,
        static (sink, ct) => sink.ReceiveAsync<DurableProbe>(default, ct)),
    ]));

    // Publish via the transport → INSERT into wh_signals.
    var (transport, _) = _createTransport(Guid.NewGuid());
    await transport.StartAsync(new CountingSink());
    await transport.PublishAsync(new DurableProbe(1), SignalTarget.Broadcast);

    // Start a fresh tail worker on a NEW instance id — its cursor initializes to MAX(id)-EXCLUSIVE
    // via COALESCE(MAX(id), 0), so it should NOT deliver the row we just inserted. Insert another
    // AFTER cursor init so the tail sees it.
    var tailInstanceId = Guid.NewGuid();
    var sink = new CountingSink();
    var tail = _createTail(tailInstanceId, sink);

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await tail.StartAsync(cts.Token);

    // Wait for cursor initialization to land — a fresh tail row should appear.
    var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
    while (DateTimeOffset.UtcNow < deadline) {
      await using var conn = new NpgsqlConnection(ConnectionString);
      await conn.OpenAsync(cts.Token);
      await using var cmd = new NpgsqlCommand(
        "SELECT COUNT(*) FROM wh_signal_cursors WHERE instance_id = @id", conn);
      cmd.Parameters.AddWithValue("id", tailInstanceId);
      var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(cts.Token) ?? 0, System.Globalization.CultureInfo.InvariantCulture);
      if (count > 0) { break; }
      await Task.Delay(100, cts.Token);
    }

    // Now publish a NEW durable signal — the tail must deliver it on its next tick.
    await transport.PublishAsync(new DurableProbe(2), SignalTarget.Broadcast);

    // Wait up to 15s for the tail to catch it (tick interval is 2s).
    var caught = false;
    var catchDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (DateTimeOffset.UtcNow < catchDeadline) {
      if (sink.Received > 0) { caught = true; break; }
      await Task.Delay(100, cts.Token);
    }

    await tail.StopAsync(CancellationToken.None);
    await Assert.That(caught).IsTrue()
      .Because("the durable tail must deliver signals persisted after its cursor was initialized");
  }
}
