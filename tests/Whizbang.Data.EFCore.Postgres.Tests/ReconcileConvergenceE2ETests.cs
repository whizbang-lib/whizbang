using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The whole reconcile loop, end to end, over TWO real Postgres stores and the REAL production
/// receptors: an origin holding the authoritative history and a consumer missing part of it.
///
/// <para>
/// audit ask (windowed types) → origin answers from real folds → type mismatch → windowed
/// drill-down → stream pages (cursor-following past page one) → deficit-only repairs → the real
/// redelivery receptor + pump select and bundle the exact window slice → the bundles land as
/// received copies → the NEXT audit round folds clean → the seal advances and the audit goes
/// quiet. This is the property the whole subsystem exists for: a deficient service converges to
/// provably complete, and then stops paying for the proof.
/// </para>
///
/// <para>
/// The wire between the two sides is an in-test message loop over capture transports — the
/// transport-level receive path (envelope binding, composite fan-out, inbox storage) is covered
/// by its own suites; THIS test pins the protocol's convergence over real SQL folds.
/// </para>
/// </summary>
/// <docs>resilience/stream-integrity</docs>
[Category("Integration")]
[Category("Shard4")]
public class ReconcileConvergenceE2ETests : EFCoreTestBase {
  private const string TYPE = "Contracts.ConvergenceProbe";
  private const string TENANT = "tenant-a";
  private const string ORIGIN_NAME = "origin-svc";
  private const string CONSUMER_NAME = "consumer-svc";
  private const string ORIGIN_REQUEST_TOPIC = "origin.requests";

  private string? _consumerDbName;
  private NpgsqlDataSource? _consumerDataSource;
  private DbContextOptions<WorkCoordinationDbContext>? _consumerDbOptions;

  [After(Test)]
  public async Task TeardownConsumerDbAsync() {
    if (_consumerDataSource is not null) {
      await _consumerDataSource.DisposeAsync();
      _consumerDataSource = null;
    }
    if (_consumerDbName is not null) {
      try {
        await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($@"
          SELECT pg_terminate_backend(pid) FROM pg_stat_activity
          WHERE datname = '{_consumerDbName}' AND pid <> pg_backend_pid()");
        await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_consumerDbName} WITH (FORCE)");
      } catch {
        // container teardown collects strays
      }
      _consumerDbName = null;
    }
  }

  /// <summary>Provisions the SECOND store (the consumer's) beside the base class's origin store —
  /// two databases, two local service ids, exactly like two services sharing nothing but the wire.</summary>
  private async Task<DbContextOptions<WorkCoordinationDbContext>> _provisionConsumerDbAsync() {
    _consumerDbName = $"test_consumer_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await admin.ExecuteAsync($"CREATE DATABASE {_consumerDbName}");
    }
    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _consumerDbName,
      Timezone = "UTC",
      IncludeErrorDetail = true
    };
    var dsBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
    dsBuilder.ConfigureJsonOptions(JsonContextRegistry.CreateCombinedOptions());
    dsBuilder.EnableDynamicJson();
    dsBuilder.UseVector();
    _consumerDataSource = dsBuilder.Build();
    var optionsBuilder = new DbContextOptionsBuilder<WorkCoordinationDbContext>();
    optionsBuilder.UseNpgsql(_consumerDataSource, o => o.UseWhizbangFunctions())
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning));
    _consumerDbOptions = optionsBuilder.Options;
    await using var ctx = new WorkCoordinationDbContext(_consumerDbOptions);
    await ctx.EnsureWhizbangDatabaseInitializedAsync();
    return _consumerDbOptions;
  }

  [Test]
  public async Task FullReconcileLoop_DeficitDetected_PagedAudit_Backfilled_SealAdvances_ThenQuietAsync() {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var consumerDbOptions = await _provisionConsumerDbAsync();

    // ── seed: the origin owns six events across three streams; the consumer holds three ────
    var s1 = Guid.NewGuid();
    var s2 = Guid.NewGuid();
    var s3 = Guid.NewGuid();
    var events = new (Guid Stream, Guid EventId, long Seq)[] {
      (s1, Guid.NewGuid(), 10), (s1, Guid.NewGuid(), 20),
      (s2, Guid.NewGuid(), 30), (s2, Guid.NewGuid(), 40),
      (s3, Guid.NewGuid(), 50), (s3, Guid.NewGuid(), 60),
    };
    await using (var originConn = new NpgsqlConnection(ConnectionString)) {
      await originConn.OpenAsync();
      foreach (var (stream, eventId, seq) in events) {
        await _seedOriginEventAsync(originConn, stream, eventId, seq);
      }
    }
    // The consumer already received s1 fully and the first event of s2 — s2 is partially
    // deficient and s3 entirely missing: both deficit shapes in one lane.
    var alreadyReceived = new[] { events[0], events[1], events[2] };
    await using (var consumerConn = new NpgsqlConnection(_consumerDataSourceConnectionString())) {
      await consumerConn.OpenAsync();
      var originId = await _localServiceIdAsync(DbContextOptions, jsonOptions);
      foreach (var (stream, eventId, seq) in alreadyReceived) {
        await _applyReceivedCopyAsync(consumerConn, stream, eventId, seq, originId);
      }
    }

    // ── the two sides: real coordinators, real receptors, capture transports ────────────────
    var originTransport = new _captureTransport();
    var consumerTransport = new _captureTransport();
    var originId2 = await _localServiceIdAsync(DbContextOptions, jsonOptions);
    var consumerOptions = new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,   // the loop under test repairs; ReportOnly is the default
      AuditSettleWindowMinutes = 0,
      MaxDigestsPerManifest = 2,    // 3 streams → 2 pages: cursor-following must fire
      PublishReportEvents = false,
      // This E2E drives the LEGACY burst loop end-to-end (compare dispatches repairs inline).
      // The paced-drain default makes the compare discovery-only; a drain-driven twin of this
      // E2E lands with the drain's AIMD increment.
      RepairDrainEnabled = false,
    };
    var originOptions = new StreamIntegrityOptions { AuditSettleWindowMinutes = 0, MaxDigestsPerManifest = 2 };

    var originProvider = _buildProvider(DbContextOptions, jsonOptions, originTransport, ORIGIN_NAME, originOptions, tracker: null);
    var consumerTracker = new IntegrityGapTracker();
    consumerTracker.RecordCheckpoint(originId2, ORIGIN_NAME, DateTimeOffset.UtcNow, ORIGIN_REQUEST_TOPIC);
    var consumerProvider = _buildProvider(consumerDbOptions, jsonOptions, consumerTransport, CONSUMER_NAME, consumerOptions, consumerTracker);

    var originRequestReceptor = new IntegrityManifestRequestReceptor(
      originProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);
    var originRedeliveryReceptor = new RedeliveryRequestReceptor(
      originProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);
    var consumerManifestReceptor = new IntegrityManifestReceptor(
      consumerProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);

    // ── round 1: audit → drill-down → paging → repairs → backfill ──────────────────────────
    await originRequestReceptor.HandleAsync(_typesAsk());
    await _pumpUntilQuietAsync(originTransport, consumerTransport,
      originRequestReceptor, originRedeliveryReceptor, consumerManifestReceptor, originId2, jsonOptions);

    var followUps = consumerTransport.Published
      .Where(p => p.EnvelopeType?.Contains("RequestIntegrityManifest") == true)
      .Select(p => _payload<RequestIntegrityManifest>(p.Envelope, jsonOptions))
      .ToList();
    await Assert.That(followUps.Any(r => r.ResumeAfterStreamId is not null)).IsTrue()
      .Because("with a 2-stream page over a 3-stream lane the audit MUST follow the cursor — " +
               "page one alone leaves a third of the lane unaudited");

    var repairs = consumerTransport.Published
      .Where(p => p.EnvelopeType?.Contains("RequestRedeliveryCommand") == true)
      .Select(p => _payload<RequestRedeliveryCommand>(p.Envelope, jsonOptions))
      .ToList();
    await Assert.That(repairs.Count).IsGreaterThan(0)
      .Because("the deficit must turn into directed repair requests");

    var bundles = originTransport.Published
      .Count(p => p.EnvelopeType?.Contains("RedeliveryComposite") == true);
    await Assert.That(bundles).IsGreaterThan(0)
      .Because("the origin's real pump must select the window slice and ship it");

    // The backfill landed: the consumer now holds all six events of the lane.
    await using (var check = new NpgsqlConnection(_consumerDataSourceConnectionString())) {
      await check.OpenAsync();
      await using var cmd = check.CreateCommand();
      cmd.CommandText = "SELECT COUNT(*) FROM wh_event_store WHERE event_type = @t";
      cmd.Parameters.AddWithValue("t", TYPE);
      var count = (long)(await cmd.ExecuteScalarAsync())!;
      await Assert.That(count).IsEqualTo(6L)
        .Because("every missing event of the window must have been backfilled exactly once");
    }

    // The folds themselves converged: origin's windowed type roll-up equals the consumer's.
    await using (var originCtx = new WorkCoordinationDbContext(DbContextOptions))
    await using (var consumerCtx = new WorkCoordinationDbContext(consumerDbOptions)) {
      var originFold = await new EFCoreWorkCoordinator<WorkCoordinationDbContext>(originCtx, jsonOptions)
        .ComputeTypeDigestsWindowedAsync(null, [TYPE], 0, null, TimeSpan.Zero);
      var consumerFold = await new EFCoreWorkCoordinator<WorkCoordinationDbContext>(consumerCtx, jsonOptions)
        .ComputeTypeDigestsWindowedAsync(originId2, [TYPE], 0, null, TimeSpan.Zero);
      await Assert.That(consumerFold!.Digests.Count).IsEqualTo(1);
      await Assert.That(originFold!.Digests.Count).IsEqualTo(1);
      await Assert.That((consumerFold.Digests[0].DigestLo, consumerFold.Digests[0].DigestHi, consumerFold.Digests[0].EventCount))
        .IsEqualTo((originFold.Digests[0].DigestLo, originFold.Digests[0].DigestHi, originFold.Digests[0].EventCount))
        .Because("after the backfill the two folds must be IDENTICAL — identical identity, not just identical counts");
    }

    // ── round 2: the SAME audit finds nothing wrong and certifies the window ───────────────
    var beforeRound2Repairs = consumerTransport.Published.Count(p => p.EnvelopeType?.Contains("RequestRedeliveryCommand") == true);
    var beforeRound2DrillDowns = consumerTransport.Published.Count(p => p.EnvelopeType?.Contains("RequestIntegrityManifest") == true);
    await originRequestReceptor.HandleAsync(_typesAsk());
    await _pumpUntilQuietAsync(originTransport, consumerTransport,
      originRequestReceptor, originRedeliveryReceptor, consumerManifestReceptor, originId2, jsonOptions);

    await Assert.That(consumerTransport.Published.Count(p => p.EnvelopeType?.Contains("RequestRedeliveryCommand") == true))
      .IsEqualTo(beforeRound2Repairs)
      .Because("a converged lane must not re-ship anything — the audit goes quiet");
    await Assert.That(consumerTransport.Published.Count(p => p.EnvelopeType?.Contains("RequestIntegrityManifest") == true))
      .IsEqualTo(beforeRound2DrillDowns)
      .Because("matching type roll-ups need no drill-down");

    await using (var sealCheck = new NpgsqlConnection(_consumerDataSourceConnectionString())) {
      await sealCheck.OpenAsync();
      await using var cmd = sealCheck.CreateCommand();
      cmd.CommandText = "SELECT sealed_through FROM wh_integrity_seals WHERE origin_service_id = @o";
      cmd.Parameters.AddWithValue("o", originId2);
      var sealedThrough = await cmd.ExecuteScalarAsync();
      await Assert.That(sealedThrough).IsNotNull()
        .Because("a clean, complete, single-chunk window is verified history — the seal is the proof");
      await Assert.That((long)sealedThrough!).IsGreaterThanOrEqualTo(60L)
        .Because("the certified watermark must cover every seeded event");
    }
  }

  // ── the in-test wire ──────────────────────────────────────────────────────

  // Wire cursors span the WHOLE test: each pump call continues from where the last one stopped —
  // re-processing an earlier round's messages would replay the audit against itself.
  private int _processedOrigin;
  private int _processedConsumer;

  /// <summary>
  /// Shuttles published messages between the two sides until both are drained. Redelivery
  /// bundles apply as received copies — identity (event id) and origin sequence preserved, which
  /// is exactly what the transport receive path (covered by its own suites) does.
  /// </summary>
  private async Task _pumpUntilQuietAsync(
      _captureTransport originTransport, _captureTransport consumerTransport,
      IntegrityManifestRequestReceptor originRequestReceptor,
      RedeliveryRequestReceptor originRedeliveryReceptor,
      IntegrityManifestReceptor consumerManifestReceptor,
      Guid originId, JsonSerializerOptions jsonOptions) {
    for (var guard = 0; guard < 200; guard++) {
      var progressed = false;
      while (_processedOrigin < originTransport.Published.Count) {
        var (envelope, _, envelopeType) = originTransport.Published[_processedOrigin++];
        progressed = true;
        if (envelopeType?.Contains("IntegrityManifest") == true) {
          await consumerManifestReceptor.HandleAsync(_payload<IntegrityManifest>(envelope, jsonOptions));
        } else if (envelopeType?.Contains("RedeliveryComposite") == true) {
          await _applyBundleAsync(_payload<RedeliveryComposite>(envelope, jsonOptions), originId);
        }
      }
      while (_processedConsumer < consumerTransport.Published.Count) {
        var (envelope, _, envelopeType) = consumerTransport.Published[_processedConsumer++];
        progressed = true;
        if (envelopeType?.Contains("RequestIntegrityManifest") == true) {
          await originRequestReceptor.HandleAsync(_payload<RequestIntegrityManifest>(envelope, jsonOptions));
        } else if (envelopeType?.Contains("RequestRedeliveryCommand") == true) {
          await originRedeliveryReceptor.HandleAsync(_payload<RequestRedeliveryCommand>(envelope, jsonOptions));
        }
      }
      if (!progressed) {
        return;
      }
    }
    throw new InvalidOperationException("wire loop did not quiesce — the protocol is not converging");
  }

  private async Task _applyBundleAsync(RedeliveryComposite bundle, Guid originId) {
    await using var conn = new NpgsqlConnection(_consumerDataSourceConnectionString());
    await conn.OpenAsync();
    for (var i = 0; i < bundle.InnerEventIds.Count; i++) {
      var seq = bundle.InnerCommitSequences?[i]
        ?? throw new InvalidOperationException("bundle lost the origin commit sequence — recounting would break");
      await _applyReceivedCopyAsync(conn, bundle.StreamId, bundle.InnerEventIds[i], seq, originId);
    }
  }

  // ── seeding / provider plumbing ───────────────────────────────────────────

  private static async Task _seedOriginEventAsync(NpgsqlConnection conn, Guid stream, Guid eventId, long seq) {
    await using (var store = conn.CreateCommand()) {
      store.CommandText = $$"""
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
           commit_sequence, flags, created_at)
        VALUES (@e, @s, @s, 'TestAggregate', @t, '{"t":"{{TENANT}}"}'::jsonb, @seq, @seq, 0,
                NOW() - INTERVAL '2 hours')
        """;
      store.Parameters.AddWithValue("e", eventId);
      store.Parameters.AddWithValue("s", stream);
      store.Parameters.AddWithValue("t", TYPE);
      store.Parameters.AddWithValue("seq", seq);
      await store.ExecuteNonQueryAsync();
    }
    await using var body = conn.CreateCommand();
    body.CommandText = """
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      VALUES (@e, '{"probe":true}'::jsonb, '{}'::jsonb)
      """;
    body.Parameters.AddWithValue("e", eventId);
    await body.ExecuteNonQueryAsync();
  }

  private static async Task _applyReceivedCopyAsync(
      NpgsqlConnection conn, Guid stream, Guid eventId, long originSeq, Guid originId) {
    await using var store = conn.CreateCommand();
    // ON CONFLICT: redelivery is at-least-once — the consumer's dedup makes the copy idempotent,
    // and this apply-leg mirrors that.
    store.CommandText = $$"""
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version,
         commit_sequence, flags, origin_service_id, origin_commit_sequence, created_at)
      VALUES (@e, @s, @s, 'TestAggregate', @t, '{"t":"{{TENANT}}"}'::jsonb, @oseq,
              nextval('wh_commit_seq'), 0, @origin, @oseq, NOW() - INTERVAL '2 hours')
      ON CONFLICT (event_id) DO NOTHING
      """;
    store.Parameters.AddWithValue("e", eventId);
    store.Parameters.AddWithValue("s", stream);
    store.Parameters.AddWithValue("t", TYPE);
    store.Parameters.AddWithValue("oseq", originSeq);
    store.Parameters.AddWithValue("origin", originId);
    await store.ExecuteNonQueryAsync();
  }

  private string _consumerDataSourceConnectionString() {
    var builder = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _consumerDbName!,
      Timezone = "UTC",
      IncludeErrorDetail = true
    };
    return builder.ConnectionString;
  }

  private static async Task<Guid> _localServiceIdAsync(
      DbContextOptions<WorkCoordinationDbContext> dbOptions, JsonSerializerOptions jsonOptions) {
    await using var ctx = new WorkCoordinationDbContext(dbOptions);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, jsonOptions);
    return await coordinator.GetLocalServiceIdAsync();
  }

  private static RequestIntegrityManifest _typesAsk() => new() {
    RequesterService = CONSUMER_NAME,
    Topic = "consumer.inbox",
    EventTypes = [TYPE],
    Level = ManifestLevel.Types,
    Windowed = true,
    SinceSequence = 0,
  };

  private static ServiceProvider _buildProvider(
      DbContextOptions<WorkCoordinationDbContext> dbOptions, JsonSerializerOptions jsonOptions,
      _captureTransport transport, string serviceName, StreamIntegrityOptions options,
      IntegrityGapTracker? tracker) {
    var services = new ServiceCollection();
    services.AddScoped<IWorkCoordinator>(_ =>
      new EFCoreWorkCoordinator<WorkCoordinationDbContext>(new WorkCoordinationDbContext(dbOptions), jsonOptions));
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IDispatcher>(new _captureDispatcher());
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));
    services.AddSingleton<IServiceInstanceProvider>(new _instanceProvider(serviceName));
    services.AddSingleton(Options.Create(options));
    services.AddSingleton<IIntegrityRepairLedger>(new IntegrityRepairLedger());
    if (tracker is not null) {
      services.AddSingleton(tracker);
    }
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination("consumer.inbox"));
    services.AddSingleton(consumerOptions);
    return services.BuildServiceProvider();
  }

  private static T _payload<T>(IMessageEnvelope envelope, JsonSerializerOptions options) =>
    (T)JsonSerializer.Deserialize(
      ((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(),
      options.GetTypeInfo(typeof(T)))!;

  private sealed class _captureTransport : ITransport {
    public List<(IMessageEnvelope Envelope, TransportDestination Destination, string? EnvelopeType)> Published { get; } = [];
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination, string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) {
      lock (Published) {
        Published.Add((envelope, destination, envelopeType));
      }
      return Task.CompletedTask;
    }
    public Task<ISubscription> SubscribeBatchAsync(Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler, TransportDestination destination, TransportBatchOptions batchOptions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope requestEnvelope, TransportDestination destination, CancellationToken cancellationToken = default) where TRequest : notnull where TResponse : notnull => throw new NotSupportedException();
  }

  private sealed class _captureDispatcher : IDispatcher {
    public List<object> Published { get; } = [];

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
      Published.Add(eventData!);
      return Task.FromResult<IDeliveryReceipt>(new _receipt());
    }

    public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, Whizbang.Core.Dispatch.DispatchOptions options) => PublishAsync(eventData);
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, Whizbang.Core.Dispatch.DispatchOptions options) where TMessage : notnull => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotSupportedException();
    public Task<IDeliveryReceipt> SendAsync(object message, IMessageContext context, Whizbang.Core.Dispatch.DispatchOptions options, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync<TMessage>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotSupportedException();
    public ValueTask LocalInvokeAsync(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, IMessageContext context, string callerMemberName = "", string callerFilePath = "", int callerLineNumber = 0) => throw new NotSupportedException();
    public ValueTask<Whizbang.Core.Dispatch.InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, Whizbang.Core.Dispatch.DispatchOptions options) => throw new NotSupportedException();
    public Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CascadeMessageAsync(IMessage message, Whizbang.Core.Dispatch.DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, Whizbang.Core.Dispatch.DispatchModes mode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull => throw new NotSupportedException();
    public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull => throw new NotSupportedException();
    public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) => throw new NotSupportedException();

    private sealed class _receipt : IDeliveryReceipt {
      public MessageId MessageId => MessageId.New();
      public CorrelationId? CorrelationId => null;
      public MessageId? CausationId => null;
      public DateTimeOffset Timestamp => DateTimeOffset.UtcNow;
      public string Destination => "test";
      public DeliveryStatus Status => DeliveryStatus.Delivered;
      public IReadOnlyDictionary<string, JsonElement> Metadata => new Dictionary<string, JsonElement>();
      public Guid? StreamId => null;
    }
  }

  private sealed class _instanceProvider(string name) : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => name;
    public string HostName => "e2e-host";
    public int ProcessId => 1;
    public ServiceInstanceInfo ToInfo() => new() {
      InstanceId = InstanceId,
      ServiceName = name,
      HostName = HostName,
      ProcessId = ProcessId,
    };
  }
}
