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
[Category("Shard2")]
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

  /// <summary>
  /// A schema gate that never opens and reports when a waiter arrives. <see cref="Entered"/> is the
  /// deterministic "the tail is parked at the barrier" signal; the infinite delay behind it then
  /// observes the stopping token exactly as the real gate's <c>Task.WaitAsync</c> does.
  /// </summary>
  /// <remarks>
  /// <para>Needed because <c>StartAsync</c> only schedules <c>ExecuteAsync</c> onto the thread pool.
  /// Asserting straight after it describes a worker whose body may never have started, and
  /// "canceled at the gate" and "never dispatched" are indistinguishable from the outside.</para>
  /// </remarks>
  private sealed class BlockingGate : Whizbang.Core.Workers.ISchemaReadyGate {
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;
    public bool IsReady => false;
    public void MarkReady() { }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken) {
      _entered.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>Generous ceiling for the worker-body signals below; the tests fail fast, not slow.</summary>
  private static readonly TimeSpan _signalWait = TimeSpan.FromSeconds(20);

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

  /// <summary>
  /// Rows this instance owns in <c>wh_signal_cursors</c> — the table the tail's first act behind
  /// the schema gate INSERTs into, and therefore the observable proof of whether it got there.
  /// </summary>
  private async Task<long> _countCursorRowsAsync(Guid instanceId, CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var cmd = new NpgsqlCommand(
      "SELECT COUNT(*) FROM wh_signal_cursors WHERE instance_id = @id", conn);
    cmd.Parameters.AddWithValue("id", instanceId);
    return Convert.ToInt64(
      await cmd.ExecuteScalarAsync(cancellationToken) ?? 0, System.Globalization.CultureInfo.InvariantCulture);
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

  // ============================================================
  // Lifecycle
  // ============================================================
  //
  // The tail is the backstop for a missed NOTIFY: a doorbell that never arrived on the wire is
  // replayed from wh_signals on the next tick. That makes an ended loop invisible and permanent —
  // the fast path keeps working, so nothing looks wrong, but every signal the wire drops from
  // then on is dropped for good.

  [Test]
  [Timeout(60000)]
  public async Task ExecuteAsync_CanceledBeforeSchemaReady_ReturnsCleanlyAsync(
      CancellationToken testToken) {
    // The first act of the loop is an INSERT into wh_signal_cursors, a table the migration
    // creates. A host that fails during migration has to get a clean shutdown here.
    var opts = new WhizbangNotificationOptions { DirectConnectionString = ConnectionString };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instanceId = Guid.CreateVersion7();
    var gate = new BlockingGate();   // never opens, and says when the tail arrives
    var worker = new PgDurableSignalTailWorker(
      Options.Create(opts), cfg,
      new ServiceInstanceProvider(instanceId, "utest-svc", "utest-host", processId: 1),
      new CountingSink(),
      NullLogger<PgDurableSignalTailWorker>.Instance,
      schemaReadyGate: gate);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);
    // StartAsync only queues ExecuteAsync on the thread pool, so wait for the body to actually
    // reach the barrier. Without this the assertions below are equally satisfied by a run that
    // was canceled before the thread pool ever invoked it.
    await gate.Entered.WaitAsync(_signalWait, testToken);

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None).WaitAsync(_signalWait, testToken);
    var executeTask = worker.ExecuteTask;
    await executeTask!.WaitAsync(_signalWait, testToken)
      .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    await Assert.That(await _countCursorRowsAsync(instanceId, testToken)).IsEqualTo(0L)
      .Because("a canceled gate wait must end the run before the cursor INSERT — that statement "
             + "targets a table the migration it is still waiting on may not have created yet");
    await Assert.That(executeTask.IsCompleted).IsTrue()
      .Because("a tail that never settles hangs shutdown instead of ending it");
    await Assert.That(executeTask.IsFaulted).IsFalse()
      .Because("a faulted tail turns an ordinary shutdown into a reported crash");
  }

  [Test]
  [Timeout(60000)]
  public async Task ExecuteAsync_WithNoReachableConnection_StopsInsteadOfSpinningAsync(
      CancellationToken testToken) {
    // With no usable connection string there is nothing to tail. Looping anyway would retry a
    // connection that can never be built, once per tick, for the life of the process.
    var opts = new WhizbangNotificationOptions { DirectConnectionString = null };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var worker = new PgDurableSignalTailWorker(
      Options.Create(opts), cfg,
      new ServiceInstanceProvider(Guid.CreateVersion7(), "utest-svc", "utest-host", processId: 1),
      new CountingSink(),
      NullLogger<PgDurableSignalTailWorker>.Instance,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);
    var executeTask = worker.ExecuteTask;

    // It returns on its own rather than waiting for cancellation.
    await executeTask!.WaitAsync(TimeSpan.FromSeconds(20), testToken);
    await worker.StopAsync(CancellationToken.None);

    await Assert.That(executeTask.IsFaulted).IsFalse();
  }

  [Test]
  [Timeout(60000)]
  public async Task ExecuteAsync_SurvivesAFailingTickAsync(CancellationToken testToken) {
    // A tick reads the database, which can be briefly unavailable. Letting that end the loop
    // would silently retire the backstop for the rest of the process.
    var opts = new WhizbangNotificationOptions {
      // Reachable on the first pass; the server refuses this connection so every tick faults.
      DirectConnectionString = "Host=127.0.0.1;Port=1;Username=nobody;Password=nobody;Database=nothing;Timeout=1",
    };
    var cfg = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var worker = new PgDurableSignalTailWorker(
      Options.Create(opts), cfg,
      new ServiceInstanceProvider(Guid.CreateVersion7(), "utest-svc", "utest-host", processId: 1),
      new CountingSink(),
      NullLogger<PgDurableSignalTailWorker>.Instance,
      schemaReadyGate: Whizbang.Core.Workers.SchemaReadyGate.AlreadyReady());

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
    await worker.StartAsync(cts.Token);

    // Long enough for the cursor init to fail and at least one tick to fail behind it.
    await Task.Delay(TimeSpan.FromSeconds(5), testToken);
    var executeTask = worker.ExecuteTask;

    await Assert.That(executeTask!.IsCompleted).IsFalse()
      .Because("a database blip must not retire the backstop for the life of the process");

    await cts.CancelAsync();
    await worker.StopAsync(CancellationToken.None);
    await Assert.That(executeTask.IsFaulted).IsFalse();
  }

}
