using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres.Notifications;
using Whizbang.Testing.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// End-to-end "commit → perspective-visible" latency lock through the REAL pipeline under
/// PRODUCTION-DEFAULT cadences. An event committed via <c>commit_handler_result</c> flows through
/// <c>_emit_event_store_chain</c> (event store + <c>wh_perspective_events</c> + NOTIFY
/// <c>wh_committed</c> + instance-routed work doorbell), the commit-order stamper stamps its
/// <c>commit_sequence</c>, <see cref="ClaimWorker"/> claims the stream, and
/// <see cref="PerspectiveWorker"/> drain-fetches and applies it into <c>wh_per_order</c>.
///
/// <para>The regression this locks out: perspective visibility quantizing to the multi-second
/// backstop cadence instead of the NOTIFY-driven sub-second path. Two known failure shapes:
/// (1) the stamper not waking on <c>wh_committed</c>, leaving events unstamped until
/// <c>get_stream_events</c>' 5 s unstamped grace elapses; (2) a stamp wake that lands while an
/// older same-database transaction holds the ordering fence going back to sleep until the next
/// backstop tick instead of retrying on <see cref="CommitOrderStamperOptions.FencedRetryInterval"/>.</para>
///
/// <para>Every options object below is constructed with PRODUCTION DEFAULTS on purpose — no
/// polling/window/batching overrides. The asserted bound is UNSCALED wall clock: if any layer
/// falls back to a 5 s cadence the measurement blows the budget and the test fails.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgCommitOrderStamperWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/ClaimWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class PerspectiveVisibilityLatencyE2ETests : EFCoreTestBase {

  /// <summary>
  /// UNSCALED wall-clock budget for commit → perspective-visible. Deliberately NOT scaled by
  /// any test-timeout multiplier: the whole point is that production-default cadences land the
  /// apply well under the 5 s backstop, so a fixed absolute bound is the lock.
  /// </summary>
  private static readonly TimeSpan _visibilityBudget = TimeSpan.FromSeconds(1.5);

  /// <summary>All real components composed the way a booting pod composes them.</summary>
  private sealed class RealPipeline {
    public required ServiceProvider Services { get; init; }
    public required ServiceInstanceProvider InstanceProvider { get; init; }
    public required PgSharedNotifyConnection SharedConnection { get; init; }
    public required PgWorkNotificationListener Listener { get; init; }
    public required PgCommitOrderStamperWorker Stamper { get; init; }
    public required ClaimWorker ClaimWorker { get; init; }
    public required PerspectiveWorker PerspectiveWorker { get; init; }
    public required Task LeaderElected { get; init; }
  }

  private RealPipeline _composePipeline() {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddScoped<IEventStore>(sp => new EFCoreEventStore<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddOptions<Whizbang.Core.Configuration.WhizbangCoreOptions>();
    // The REAL generated runner registry (OrderPerspective ← SampleOrderCreatedEvent) — the same
    // registration production wiring invokes via the module-initializer callback. Registers
    // IPerspectiveRunnerRegistry + IEventTypeProvider + the scoped runners.
    services.AddPerspectiveRunners();
    // The real perspective persistence surface: IPerspectiveStore<Order> upserting wh_per_order.
    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services, typeof(WorkCoordinationDbContext), typeof(Order), "wh_per_order",
      new PostgresUpsertStrategy());
    var provider = services.BuildServiceProvider();
    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

    var gate = new SchemaReadyGate();
    gate.MarkReady();

    var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    // ONE instance identity everywhere: the listener's wh_work_i_<id> channel, the claim loop,
    // the perspective drain fetch, and the commit's instance_id must agree or leases and
    // doorbells route past each other.
    var instanceProvider = new ServiceInstanceProvider(config);

    // Production defaults on purpose — this test locks latency under real cadences; do not
    // neuter. Only the connection string + signaling mode are set; no polling overrides.
    var notificationOptions = new WhizbangNotificationOptions {
      DirectConnectionString = ConnectionString,
      SignalingMode = WorkSignalingMode.ListenNotify
    };

    var sharedConnection = new PgSharedNotifyConnection(
      Options.Create(notificationOptions),
      config,
      instanceProvider,
      NullLogger<PgSharedNotifyConnection>.Instance,
      connectionStringFallback: null,
      timeProvider: null);
    var listener = new PgWorkNotificationListener(
      sharedConnection, sharedConnection, instanceProvider,
      NullLogger<PgWorkNotificationListener>.Instance);

    // Production defaults on purpose (PollingInterval 250 ms, FencedRetryInterval 250 ms,
    // LeaderElectionRetry 1.5 s) — this test locks latency under real cadences; do not neuter.
    var stamper = new PgCommitOrderStamperWorker(
      Options.Create(notificationOptions),
      Options.Create(new CommitOrderStamperOptions()),
      config,
      sharedConnection,
      NullLogger<PgCommitOrderStamperWorker>.Instance);
    var leaderTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    stamper.OnBecameLeader += () => leaderTcs.TrySetResult();

    // Same concrete channel classes production DI wires (WorkerPipelineExtensions): ClaimWorker
    // writes claimed perspective stream ids into the SAME IPerspectiveDrainChannel instance the
    // PerspectiveWorker consumes.
    var harness = new PerspectiveWorkerTestHarness();

    // Production defaults on purpose (base 250 ms poll, 10 s adaptive cap) — this test locks
    // latency under real cadences; do not neuter. The NOTIFY signaling gate is wired exactly
    // as production DI wires it (INotifySignalingGate → the PgSharedNotifyConnection
    // singleton), so a healthy gate relaxes the claim base cadence to
    // NotifyHealthyPollingIntervalMilliseconds (5 s default) and doorbell wakes carry the
    // fast path — the same shape a booting pod runs.
    var claimWorker = new ClaimWorker(
      scopeFactory,
      instanceProvider,
      listener,
      gate,
      Options.Create(new ClaimWorkerOptions()),
      NullLogger<ClaimWorker>.Instance,
      perspectiveChannel: harness.ChannelWriter,
      perspectiveDrainChannel: harness.DrainChannel,
      signalingGate: sharedConnection);

    // Production defaults on purpose (300 ms drain sliding window, 4 drain consumers,
    // burst-driven wake via the NOTIFY listener) — this test locks latency under real
    // cadences; do not neuter.
    var perspectiveWorker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: scopeFactory,
      options: Options.Create(new PerspectiveWorkerOptions()),
      logger: NullLogger<PerspectiveWorker>.Instance,
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: harness.CompletionCapture,
      failureChannel: harness.FailureCapture,
      perspectiveDrainChannel: harness.DrainChannel,
      perspectiveNotificationListener: listener,
      schemaReadyGate: gate);

    return new RealPipeline {
      Services = provider,
      InstanceProvider = instanceProvider,
      SharedConnection = sharedConnection,
      Listener = listener,
      Stamper = stamper,
      ClaimWorker = claimWorker,
      PerspectiveWorker = perspectiveWorker,
      LeaderElected = leaderTcs.Task
    };
  }

  /// <summary>
  /// Startup sequence + readiness gates (bounded waits, all BEFORE the measurement window —
  /// same style as ClaimWorkerNotificationWakeIntegrationTests). If the NOTIFY gate is
  /// unhealthy everything falls back to tight polling and the measured numbers lie, so the
  /// health asserts here refuse to open the measurement window.
  /// </summary>
  private static async Task _startAndAwaitReadyAsync(RealPipeline pipeline, CancellationToken ct) {
    await ((IHostedService)pipeline.SharedConnection).StartAsync(ct);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
    while (!pipeline.SharedConnection.IsAvailable && DateTimeOffset.UtcNow < deadline) { await Task.Delay(50, ct); }
    await ((IHostedService)pipeline.Listener).StartAsync(ct);

    await pipeline.Stamper.StartAsync(ct);
    await pipeline.LeaderElected.WaitAsync(TimeSpan.FromSeconds(15), ct);

    await pipeline.ClaimWorker.StartAsync(ct);
    await pipeline.PerspectiveWorker.StartAsync(ct);
    await pipeline.PerspectiveWorker.StartupScanComplete.WaitAsync(TimeSpan.FromSeconds(15), ct);
    while (!pipeline.Listener.IsHealthy && DateTimeOffset.UtcNow < deadline) { await Task.Delay(50, ct); }

    await Assert.That(pipeline.SharedConnection.IsAvailable).IsTrue()
      .Because("an unavailable shared NOTIFY connection degrades every worker to backstop polling — the latency measurement would lie");
    await Assert.That(pipeline.Listener.IsHealthy).IsTrue()
      .Because("an unhealthy listener degrades ClaimWorker/PerspectiveWorker to backstop polling — the latency measurement would lie");
  }

  /// <summary>Stops everything in reverse start order.</summary>
  private static async Task _stopAsync(RealPipeline pipeline) {
    await pipeline.PerspectiveWorker.StopAsync(CancellationToken.None);
    await pipeline.ClaimWorker.StopAsync(CancellationToken.None);
    await pipeline.Stamper.StopAsync(CancellationToken.None);
    await ((IHostedService)pipeline.Listener).StopAsync(CancellationToken.None);
    await ((IHostedService)pipeline.SharedConnection).StopAsync(CancellationToken.None);
    await pipeline.Services.DisposeAsync();
  }

  [Test]
  [Timeout(120000)]
  public async Task CommitToPerspectiveVisible_ProductionDefaultCadences_LandsUnder1500msAsync(CancellationToken cancellationToken) {
    var pipeline = _composePipeline();
    try {
      await using var conn = new NpgsqlConnection(ConnectionString);
      await conn.OpenAsync(cancellationToken);
      await _registerInstanceAsync(conn, pipeline.InstanceProvider.InstanceId);
      await _seedPerspectiveAssociationAsync(conn);

      await _startAndAwaitReadyAsync(pipeline, cancellationToken);

      var streamId = (Guid)TrackedGuid.NewMedo();
      var eventId = (Guid)TrackedGuid.NewMedo();

      // Completion signal: fires after the wh_per_order upsert committed. Subscribed BEFORE
      // the commit; the TCS carries the elapsed time captured the moment the event fired.
      var sw = new Stopwatch();
      var applied = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
      pipeline.PerspectiveWorker.OnPerspectiveEventProcessed += e => {
        if (e.StreamId == streamId) {
          applied.TrySetResult(sw.Elapsed);
        }
      };

      var request = _buildCommitRequest(pipeline.InstanceProvider, streamId, eventId);
      sw.Start();
      await _commitViaHandlerResultAsync(conn, request, cancellationToken);

      var elapsed = await applied.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
      Console.WriteLine($"commit → perspective-visible elapsed: {elapsed.TotalMilliseconds:F0} ms (budget {_visibilityBudget.TotalMilliseconds:F0} ms)");

      await Assert.That(elapsed).IsLessThan(_visibilityBudget)
        .Because("under production-default cadences the NOTIFY-driven path (wh_committed wake → stamp → "
               + "instance-routed doorbell → claim → drain window → apply) lands well under the 5 s backstop; "
               + "anything near 5 s means visibility quantized to a backstop cadence");
      await Assert.That(await _countOrderRowsAsync(conn, streamId, cancellationToken)).IsEqualTo(1L)
        .Because("the completion signal fires after the wh_per_order upsert committed — the row must be readable");
    } finally {
      await _stopAsync(pipeline);
    }
  }

  [Test]
  [Timeout(120000)]
  public async Task CommitToPerspectiveVisible_FencedByOpenSameDbTransaction_StillLandsUnder1500msAsync(CancellationToken cancellationToken) {
    var pipeline = _composePipeline();
    try {
      await using var conn = new NpgsqlConnection(ConnectionString);
      await conn.OpenAsync(cancellationToken);
      await _registerInstanceAsync(conn, pipeline.InstanceProvider.InstanceId);
      await _seedPerspectiveAssociationAsync(conn);

      await _startAndAwaitReadyAsync(pipeline, cancellationToken);

      // The fence: a same-database write transaction opened BEFORE the commit. The temp-table
      // INSERT forces xid assignment, so the per-database ordering fence holds every row
      // committed after this point unstamped until the blocker resolves.
      await using var blockerConn = new NpgsqlConnection(ConnectionString);
      await blockerConn.OpenAsync(cancellationToken);
      await using var blockerTx = await blockerConn.BeginTransactionAsync(cancellationToken);
      await using (var blockerCmd = blockerConn.CreateCommand()) {
        blockerCmd.Transaction = blockerTx;
        blockerCmd.CommandText =
          "CREATE TEMP TABLE wh_fence_blocker(x int) ON COMMIT DROP; INSERT INTO wh_fence_blocker VALUES (1)";
        _ = await blockerCmd.ExecuteNonQueryAsync(cancellationToken);
      }

      var streamId = (Guid)TrackedGuid.NewMedo();
      var eventId = (Guid)TrackedGuid.NewMedo();

      var sw = new Stopwatch();
      var applied = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
      pipeline.PerspectiveWorker.OnPerspectiveEventProcessed += e => {
        if (e.StreamId == streamId) {
          applied.TrySetResult(sw.Elapsed);
        }
      };

      // Elapsed is measured from the commit call — the fence hold below is INSIDE the budget.
      var request = _buildCommitRequest(pipeline.InstanceProvider, streamId, eventId);
      sw.Start();
      await _commitViaHandlerResultAsync(conn, request, cancellationToken);

      // Scenario hold, NOT a poll: the commit's own wh_committed wake has already fired and
      // stamped ZERO rows (fenced). Keeping the blocker open ~300 ms models an older in-flight
      // transaction outliving the commit's wake. After rollback there is NO further external
      // wake for the stamper — only CommitOrderStamperOptions.FencedRetryInterval (250 ms
      // production default) can stamp the row. Without it, the stamp waits for the next
      // backstop tick (5 s) and this test blows its budget.
      await Task.Delay(300, cancellationToken);
      await blockerTx.RollbackAsync(cancellationToken);

      var elapsed = await applied.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
      Console.WriteLine($"commit → perspective-visible elapsed (fenced): {elapsed.TotalMilliseconds:F0} ms (budget {_visibilityBudget.TotalMilliseconds:F0} ms, fence held 300 ms)");

      await Assert.That(elapsed).IsLessThan(_visibilityBudget)
        .Because("the 1.5 s budget covers the 300 ms fence hold + ≤250 ms fenced re-stamp retry + "
               + "claim/drain/apply; a stamp that sleeps until the next backstop tick quantizes "
               + "perspective visibility to the backstop cadence");
      await Assert.That(await _countOrderRowsAsync(conn, streamId, cancellationToken)).IsEqualTo(1L)
        .Because("the completion signal fires after the wh_per_order upsert committed — the row must be readable");
    } finally {
      await _stopAsync(pipeline);
    }
  }

  // ============================================================================
  // helpers
  // ============================================================================

  /// <summary>
  /// claim_work only hands work to registered instances; register the pipeline's identity
  /// before any worker starts (ClaimWorker's initial heartbeat would also do this, but the
  /// commit's owner-routed NOTIFY must already resolve to a live instance).
  /// </summary>
  private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId) {
    await using var reg = conn.CreateCommand();
    reg.CommandText = @"
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
      VALUES (@id, 'latency-svc', 'latency-host', 1, NOW(), NOW(), '{}'::jsonb)
      ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()";
    reg.Parameters.AddWithValue("id", instanceId);
    await reg.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Registers the perspective association so _emit_event_store_chain auto-creates the
  /// wh_perspective_events row for OrderPerspective when the event lands in wh_event_store.
  /// Uses the framework's own type-name helpers so the stored strings match what the
  /// generated registry and the SQL normalizer produce.
  /// </summary>
  private static async Task _seedPerspectiveAssociationAsync(NpgsqlConnection conn) {
    var eventType = TypeNameFormatter.Format(typeof(SampleOrderCreatedEvent));
    var perspectiveName = TypeNameFormatter.GetPerspectiveName(typeof(OrderPerspective));
    await using var assoc = conn.CreateCommand();
    assoc.CommandText = @"
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name,
         normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @eventType, 'perspective', @target, 'latency-svc',
              @eventType, NOW(), NOW())
      ON CONFLICT DO NOTHING";
    assoc.Parameters.AddWithValue("eventType", eventType);
    assoc.Parameters.AddWithValue("target", perspectiveName);
    await assoc.ExecuteNonQueryAsync();
  }

  /// <summary>
  /// Builds the commit_handler_result request carrying one handler-emitted event. The envelope
  /// is a REAL MessageEnvelope serialized with the combined JSON options, so the payload the
  /// SQL chain extracts into wh_event_body round-trips through the drain path's typed
  /// deserialization into SampleOrderCreatedEvent.
  /// </summary>
  private static string _buildCommitRequest(IServiceInstanceProvider instance, Guid streamId, Guid eventId) {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var payload = new SampleOrderCreatedEvent { OrderId = new TestOrderId(streamId), Amount = 118.42m };
    var payloadJson = JsonSerializer.Serialize(payload, jsonOptions.GetTypeInfo(typeof(SampleOrderCreatedEvent)));
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(eventId),
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
      Payload = JsonDocument.Parse(payloadJson).RootElement
    };
    var envelopeJson = JsonSerializer.Serialize(envelope, jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
    var eventType = TypeNameFormatter.Format(typeof(SampleOrderCreatedEvent));

    return $$"""
      {
        "instance_id": "{{instance.InstanceId}}",
        "service_name": "{{instance.ServiceName}}",
        "host_name": "{{instance.HostName}}",
        "process_id": {{instance.ProcessId}},
        "new_outbox_messages": [{
          "MessageId": "{{eventId}}",
          "Destination": "order-events",
          "MessageType": "{{eventType}}",
          "EnvelopeType": null,
          "Envelope": {{envelopeJson}},
          "Metadata": {},
          "Scope": null,
          "StreamId": "{{streamId}}",
          "IsEvent": true
        }]
      }
      """;
  }

  /// <summary>The real write path: one atomic commit_handler_result call.</summary>
  private static async Task _commitViaHandlerResultAsync(NpgsqlConnection conn, string request, CancellationToken ct) {
    await using var call = conn.CreateCommand();
    call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
    call.Parameters.AddWithValue("req", request);
    _ = await call.ExecuteScalarAsync(ct);
  }

  private static async Task<long> _countOrderRowsAsync(NpgsqlConnection conn, Guid streamId, CancellationToken ct) {
    await using var verify = conn.CreateCommand();
    verify.CommandText = "SELECT count(*) FROM wh_per_order WHERE id = @sid";
    verify.Parameters.AddWithValue("sid", streamId);
    return (long)(await verify.ExecuteScalarAsync(ct))!;
  }
}
