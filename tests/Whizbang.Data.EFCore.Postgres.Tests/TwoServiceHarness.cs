// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>The audit a cycle runs.</summary>
internal enum TwoServiceAudit {
  /// <summary>The steady-state audit: a windowed type-level manifest from the consumer's seal, drill-down and repair.</summary>
  TypesWindowed,
  /// <summary>The table-driven type-level manifest (no window), drill-down and repair.</summary>
  TypesTable,
  /// <summary>A stream-level manifest straight away, and repair.</summary>
  Streams,
}

/// <summary>An event the origin emits: its stream, type, flags, superseding key and payload (JSON).</summary>
internal readonly record struct TwoServiceEmit(Guid Stream, string Type, int Flags, string? Key, string Payload, Guid? Id = null);

/// <summary>One side's store.</summary>
internal sealed record TwoServiceStore(string Name, string ConnectionString, DbContextOptions<WorkCoordinationDbContext> Options);

/// <summary>What the wire has carried so far: repair requests the consumer sent, divergence reports it raised, events the
/// origin redelivered, and redelivered events the consumer stored.</summary>
internal readonly record struct TwoServiceTally(int RepairRequests, int Reports, int Redelivered, int Restored);

/// <summary>
/// Two services over two real Postgres stores and the real integrity receptors between them: an ORIGIN that emits events
/// through its outbox emit chain, and a CONSUMER that receives them through its inbox and the claim's emit chain. The
/// consumer is the test's own store, so a perspective pipeline composed over it sees what the wire stored. Every cycle
/// is pumped until both sides are quiet; nothing waits on a clock.
/// </summary>
/// <remarks>Taken from the cleanup-and-integrity matrix's two-store harness (#2342); the two are to be merged into one.</remarks>
internal sealed class TwoServiceHarness : IAsyncDisposable {
  public const string ORIGIN_NAME = "origin-svc";
  public const string CONSUMER_NAME = "consumer-svc";
  public const string ORIGIN_REQUEST_TOPIC = "origin.requests";
  public const string CONSUMER_TOPIC = "consumer.inbox";
  public const int FLAG_SUPERSEDING = 128;

  private static readonly DateTimeOffset _t0 = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

  private readonly JsonSerializerOptions _json = JsonContextRegistry.CreateCombinedOptions();
  private readonly IntegrityWireTransport _originTransport = new();
  private readonly IntegrityWireTransport _consumerTransport = new();
  private readonly IntegrityWireDispatcher _consumerDispatcher = new();
  private readonly List<Guid> _restored = [];
  private readonly List<Guid> _redelivered = [];
  private readonly Dictionary<Guid, (TwoServiceEmit Emit, long Seq)> _emitted = [];
  private readonly Dictionary<string, EventFlags> _markers;
  private string _originDbName = "";
  private NpgsqlDataSource? _originDataSource;
  private ServiceProvider? _originProvider;
  private ServiceProvider? _consumerProvider;
  private IntegrityManifestRequestReceptor _originRequestReceptor = null!;
  private RedeliveryRequestReceptor _originRedeliveryReceptor = null!;
  private RequestOriginPositionsReceptor _originPositionsReceptor = null!;
  private OriginPositionsAnswerReceptor _consumerPositionsReceptor = null!;
  private IntegrityManifestReceptor _consumerManifestReceptor = null!;
  private int _processedOrigin;
  private int _processedConsumer;
  private DateTimeOffset _clock = _t0;
  private TimeProvider? _consumerClock;
  private Func<ServiceProvider>? _buildConsumer;

  private TwoServiceHarness(Dictionary<string, EventFlags> markers) => _markers = markers;

  public TwoServiceStore Origin { get; private set; } = null!;
  public TwoServiceStore Consumer { get; private set; } = null!;
  public Guid OriginId { get; private set; }

  /// <summary>Provisions the origin's store beside the consumer's and wires the real receptors between them.</summary>
  /// <param name="consumerConnectionString">The consumer's store: the test's own.</param>
  /// <param name="consumerOptions">Its context options.</param>
  /// <param name="markers">The flags the consumer's catalog declares, by CLR type name (the part before the comma).</param>
  /// <param name="consumedEventTypes">What the consumer's audit says it consumes (#2349); null keeps the permissive
  /// registry's list.</param>
  /// <param name="consumerClock">The clock the consumer's audit worker runs on; null leaves the system clock.</param>
  /// <param name="originRules">The origin's eligibility rules (#2362); null knows no type, so it compares versions only.</param>
  /// <param name="consumerRules">The consumer's eligibility rules; null knows no type.</param>
  public static async Task<TwoServiceHarness> CreateAsync(
      string consumerConnectionString, DbContextOptions<WorkCoordinationDbContext> consumerOptions,
      Dictionary<string, EventFlags> markers, IReadOnlyList<string>? consumedEventTypes = null, TimeProvider? consumerClock = null,
      IRuleFingerprintSource? originRules = null, IRuleFingerprintSource? consumerRules = null, bool repairDrainEnabled = false,
      StreamIntegrityMetrics? consumerMetrics = null, Action<StreamIntegrityOptions>? configure = null) {
    var originDbName = (await EFCoreSchemaTemplate.CreateDatabaseAsync("origin")).Name;
    var h = new TwoServiceHarness(markers) {
      Consumer = new TwoServiceStore("consumer", consumerConnectionString, consumerOptions),
      _originDbName = originDbName,
      _consumerClock = consumerClock,
    };
    var cs = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = originDbName,
      Timezone = "UTC",
      IncludeErrorDetail = true,
    }.ConnectionString;
    (h._originDataSource, var originOptions) = EFCoreTestBase.CreateContextOptions(cs);
    h.Origin = new TwoServiceStore("origin", cs, originOptions);
    h.OriginId = await h._localServiceIdAsync(h.Origin);

    var both = new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      AuditSettleWindowMinutes = 0,
      // Every cycle is news: no cooldown and no backoff hide a divergence or a repeated repair.
      DivergenceReportCooldownMinutes = 0,
      RepairRequestBackoffSeconds = 0,
      MaxRepairAttemptsPerBucket = 1000,
      PublishReportEvents = true,
      // The compare dispatches its repairs inline, so a cycle is one audit; with the drain on (the default in a real
      // service), the audit records its deficits and RunConsumerDrainAsync sends them.
      RepairDrainEnabled = repairDrainEnabled,
      RepairDrainRatePerSecond = 1000,
    };
    configure?.Invoke(both);
    h._originProvider = h._buildProvider(h.Origin, h._originTransport, new IntegrityWireDispatcher(), ORIGIN_NAME, both, tracker: null,
      rules: originRules);
    var tracker = new IntegrityGapTracker();
    tracker.RecordCheckpoint(h.OriginId, ORIGIN_NAME, DateTimeOffset.UnixEpoch, ORIGIN_REQUEST_TOPIC);
    h._buildConsumer = () => h._buildProvider(h.Consumer, h._consumerTransport, h._consumerDispatcher, CONSUMER_NAME, both, tracker,
      consumedEventTypes, consumerClock, consumerRules, consumerMetrics);
    h._consumerProvider = h._buildConsumer();
    h._originRequestReceptor = new IntegrityManifestRequestReceptor(
      h._originProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestRequestReceptor>.Instance);
    h._originRedeliveryReceptor = new RedeliveryRequestReceptor(
      h._originProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RedeliveryRequestReceptor>.Instance);
    h._consumerManifestReceptor = new IntegrityManifestReceptor(
      h._consumerProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    h._originPositionsReceptor = new RequestOriginPositionsReceptor(
      h._originProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RequestOriginPositionsReceptor>.Instance);
    h._consumerPositionsReceptor = new OriginPositionsAnswerReceptor(
      h._consumerProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<OriginPositionsAnswerReceptor>.Instance);
    return h;
  }

  /// <summary>
  /// #2377: the consumer restarts. Its services, and with them its pair guard, are built again over the same store, so
  /// whatever the guard knew in memory is gone and only what the store kept carries over.
  /// </summary>
  public async Task RestartConsumerAsync() {
    await _consumerProvider!.DisposeAsync();
    _consumerProvider = _buildConsumer!();
    _consumerManifestReceptor = new IntegrityManifestReceptor(
      _consumerProvider.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
  }

  public async ValueTask DisposeAsync() {
    if (_originProvider is not null) {
      await _originProvider.DisposeAsync();
    }
    if (_consumerProvider is not null) {
      await _consumerProvider.DisposeAsync();
    }
    if (_originDataSource is not null) {
      await _originDataSource.DisposeAsync();
    }
    if (_originDbName.Length > 0) {
      NpgsqlConnection.ClearPool(new NpgsqlConnection(Origin.ConnectionString));
      await PerTestDatabaseFactory.DropAsync(_originDbName);
    }
  }

  // ----- writing -----

  /// <summary>Emits the events on the origin through the outbox emit chain, as one batch, then stamps their commit
  /// sequences. Ids are ordered, so a later event is also the newer one by id and by the origin's sequence, unless an
  /// event names its own id (a derived one, which sorts at its cause).</summary>
  public async Task<Guid[]> EmitAsync(params TwoServiceEmit[] events) {
    Guid[] minted = [.. Enumerable.Range(0, events.Length).Select(_ => (Guid)TrackedGuid.New()).Order()];
    Guid[] ids = [.. events.Select((e, i) => e.Id ?? minted[i])];
    _clock = _clock.AddMinutes(1);
    await using var conn = await _openAsync(Origin);
    var instanceId = (Guid)TrackedGuid.New();
    for (var i = 0; i < events.Length; i++) {
      await _executeAsync(conn, """
        INSERT INTO wh_outbox
          (message_id, destination, message_type, envelope_type, event_data, metadata, scope,
           status, attempts, instance_id, lease_expiry, partition_number, stream_id, is_event, flags, created_at)
        VALUES
          (@id, 'two-service-topic', @type, 'env', jsonb_build_object('p', @payload::jsonb),
           CASE WHEN @key::text IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('sk', @key::text) END, NULL,
           1, 0, @inst, @lease, 0, @sid, true, @flags, @now)
        """,
        ("id", ids[i]), ("type", events[i].Type), ("payload", events[i].Payload), ("key", (object?)events[i].Key ?? DBNull.Value),
        ("inst", instanceId), ("lease", _clock.AddMinutes(5)), ("sid", events[i].Stream), ("flags", events[i].Flags), ("now", _clock));
    }
    await using (var emit = conn.CreateCommand()) {
      emit.CommandText = "SELECT _emit_event_store_chain(@ids, @inst, @lease, @now, 10000)";
      emit.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids);
      emit.Parameters.AddWithValue("inst", instanceId);
      emit.Parameters.AddWithValue("lease", _clock.AddMinutes(5));
      emit.Parameters.AddWithValue("now", _clock);
      _ = await emit.ExecuteScalarAsync();
    }
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    for (var i = 0; i < events.Length; i++) {
      var seq = await _scalarAsync(conn, "SELECT commit_sequence FROM wh_event_store WHERE event_id = @id", ("id", ids[i]));
      _emitted[ids[i]] = (events[i], seq);
    }
    return ids;
  }

  /// <summary>Delivers emitted events to the consumer live, in the order given: through its inbox, with the key and flags
  /// the wire carries and the origin's identity. <paramref name="withSequence"/> false delivers them without the origin's
  /// commit sequence (zero on the wire), as an origin that does not stamp one sends them (held without a sequence). Returns the ids
  /// stored.</summary>
  public async Task<List<Guid>> DeliverAsync(IReadOnlyList<Guid> ids, bool withSequence = true) {
    var stored = new List<Guid>();
    foreach (var id in ids) {
      var (e, seq) = _emitted[id];
      stored.AddRange(await _storeOnConsumerAsync([_inboxRow(id, e, withSequence ? seq : 0, restoreIfMissing: false)]));
    }
    return stored;
  }

  private InboxMessage _inboxRow(Guid id, TwoServiceEmit e, long seq, bool restoreIfMissing) {
    using var payload = JsonDocument.Parse(e.Payload);
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(id),
      Payload = payload.RootElement.Clone(),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Outbox, Source = MessageSource.Outbox },
      SourceServiceId = OriginId,
      SourceCommitSequence = seq,
    };
    return new InboxMessage {
      MessageId = id,
      HandlerName = "TwoServiceHandler",
      Envelope = envelope,
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = e.Type,
      StreamId = e.Stream,
      IsEvent = true,
      Flags = (EventFlags)e.Flags,
      Metadata = new EnvelopeMetadata { MessageId = envelope.MessageId, Hops = [], SupersedingKey = e.Key },
      SourceServiceId = OriginId,
      SourceCommitSequence = seq,
      RestoreIfMissing = restoreIfMissing,
    };
  }

  /// <summary>Stores rows in the consumer's inbox and runs the claim's emit chain over them; returns the ids that reached
  /// the event store.</summary>
  private async Task<List<Guid>> _storeOnConsumerAsync(InboxMessage[] rows, Func<Task>? afterInbox = null) {
    if (rows.Length == 0) {
      return [];
    }
    _clock = _clock.AddMinutes(1);
    await using var ctx = new WorkCoordinationDbContext(Consumer.Options);
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, _json);
    await coordinator.StoreInboxMessagesAsync(rows, partitionCount: 100);
    if (afterInbox is not null) {
      await afterInbox();
    }
    await using var conn = await _openAsync(Consumer);
    Guid[] ids = [.. rows.Select(r => r.MessageId)];
    var before = await _idsPresentAsync(conn, ids);
    var instanceId = (Guid)TrackedGuid.New();
    await _executeAsync(conn,
      "UPDATE wh_inbox_state SET instance_id = @inst, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = ANY(@ids)",
      ("inst", instanceId), ("ids", ids));
    await _executeAsync(conn, "SELECT _emit_event_store_chain_for_inbox(@inst, NOW() + INTERVAL '5 minutes', @now, 4)",
      ("inst", instanceId), ("now", _clock));
    // Settled inbox rows leave, so a later redelivery of the same id is judged by the event store, as in production.
    await _executeAsync(conn, "DELETE FROM wh_inbox_state WHERE message_id = ANY(@ids)", ("ids", ids));
    await _executeAsync(conn, "DELETE FROM wh_inbox WHERE message_id = ANY(@ids)", ("ids", ids));
    await _executeAsync(conn, "SELECT stamp_pending_commit_sequences(1000)");
    var after = await _idsPresentAsync(conn, ids);
    return [.. after.Except(before)];
  }

  private static async Task<HashSet<Guid>> _idsPresentAsync(NpgsqlConnection conn, Guid[] ids) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_id FROM wh_event_store WHERE event_id = ANY(@ids)";
    cmd.Parameters.AddWithValue(nameof(ids), ids);
    var set = new HashSet<Guid>();
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      set.Add(reader.GetGuid(0));
    }
    return set;
  }

  // ----- the audit -----

  /// <summary>One audit cycle of the given kind over the given types, pumped until both sides are quiet.</summary>
  public Task AuditAsync(TwoServiceAudit audit, params string[] eventTypes) => AuditAsync(audit, stampRules: true, eventTypes);

  /// <summary>One audit cycle; <paramref name="stampRules"/> false asks as a consumer on a release that predates the rule
  /// fingerprint (#2362), with none.</summary>
  public async Task AuditAsync(TwoServiceAudit audit, bool stampRules, params string[] eventTypes) {
    var ask = new RequestIntegrityManifest {
      RuleFingerprint = stampRules ? ConsumerGuard.Stamp(eventTypes) : null,
      RequesterService = CONSUMER_NAME,
      Topic = CONSUMER_TOPIC,
      EventTypes = eventTypes,
      Level = audit == TwoServiceAudit.Streams ? ManifestLevel.Streams : ManifestLevel.Types,
      Windowed = audit == TwoServiceAudit.TypesWindowed,
      SinceSequence = audit == TwoServiceAudit.TypesWindowed ? await SealAsync() : 0,
    };
    await _originRequestReceptor.HandleAsync(ask);
    await _pumpUntilQuietAsync();
  }

  /// <summary>How far the consumer's seal for the origin has advanced (0 when it has none).</summary>
  public async Task<long> SealAsync() {
    await using var conn = await _openAsync(Consumer);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT sealed_through FROM wh_integrity_seals WHERE origin_service_id = @o";
    cmd.Parameters.AddWithValue("o", OriginId);
    var v = await cmd.ExecuteScalarAsync();
    return v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
  }

  /// <summary>
  /// One cycle of the consumer's real audit worker (#2349) with <paramref name="options"/>, pumped until both sides are
  /// quiet. The cycle claim is released first, as the claim window's passing would release it: each call is one cycle.
  /// </summary>
  public async Task RunConsumerAuditAsync(StreamIntegrityOptions options) {
    await ExecuteAsync(Consumer, "DELETE FROM wh_settings WHERE setting_key = 'integrity_audit_last_run'");
    var worker = new IntegrityAuditWorker(
      _consumerProvider!.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), NullLogger<IntegrityAuditWorker>.Instance);
    await worker.RunAuditOnceAsync(CancellationToken.None);
    await _pumpUntilQuietAsync();
  }

  /// <summary>
  /// #2544: one maintenance cycle of the consumer's origin-position fill, pumped until both sides are quiet: the consumer
  /// asks the origin for the positions of the events it holds without one, and the origin's answer fills them.
  /// </summary>
  public async Task<OriginPositionFillCycle> RunConsumerPositionFillAsync() {
    await using var scope = _consumerProvider!.CreateAsyncScope();
    var cycle = await new OriginPositionFillStep(NullLogger.Instance).FillAsync(scope.ServiceProvider, CancellationToken.None);
    await _pumpUntilQuietAsync();
    return cycle;
  }

  /// <summary>
  /// One pass of the consumer's paced repair drain (#2365), pumped until both sides are quiet: the deficits an audit
  /// recorded are requested of the origin, and its answers applied.
  /// </summary>
  public async Task RunConsumerDrainAsync() {
    var worker = new RepairDrainWorker(
      _consumerProvider!.GetRequiredService<IServiceScopeFactory>(),
      _consumerProvider!.GetRequiredService<IOptions<StreamIntegrityOptions>>(), NullLogger<RepairDrainWorker>.Instance);
    await worker.DrainOnceAsync(CancellationToken.None);
    await _pumpUntilQuietAsync();
  }

  /// <summary>
  /// The origin's checkpoint reaches the consumer: its highest stamped position, as the checkpoint announces it, received
  /// at the consumer's clock (the harness clock when the consumer runs on none).
  /// </summary>
  public async Task CheckpointAsync() {
    long head;
    await using (var conn = await _openAsync(Origin)) {
      head = await _scalarAsync(conn, "SELECT COALESCE(MAX(commit_sequence), 0) FROM wh_event_store");
    }
    await using var ctx = new WorkCoordinationDbContext(Consumer.Options);
    await new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, _json)
      .IntegrityRecordOriginCheckpointAsync(OriginId, ORIGIN_NAME, ORIGIN_REQUEST_TOPIC, _consumerClock?.GetUtcNow() ?? _clock, head);
  }

  /// <summary>
  /// The origin rewrites history a consumer verified (#2365): the rewrite is noted from position 0 and the generation
  /// bumped, as a reclassification or a refold does.
  /// </summary>
  public Task RewriteOriginHistoryAsync() => ExecuteAsync(Origin, """
    DO $$ BEGIN PERFORM _wh_note_origin_rewrite(0); PERFORM _wh_bump_origin_generation(); END $$
    """);

  /// <summary>The origin's pair guard (#2362).</summary>
  public IntegrityPairGuard OriginGuard => _originProvider!.GetRequiredService<IntegrityPairGuard>();

  /// <summary>The consumer's pair guard (#2362).</summary>
  public IntegrityPairGuard ConsumerGuard => _consumerProvider!.GetRequiredService<IntegrityPairGuard>();

  /// <summary>True makes the origin answer as a release that predates the rule fingerprint (#2362): its manifests carry
  /// none.</summary>
  public bool OriginPredatesRuleFingerprint { get; set; }

  /// <summary>The manifest asks the consumer has sent so far.</summary>
  public int ManifestAsks() =>
    _consumerTransport.Published.Count(p => p.EnvelopeType?.Contains("RequestIntegrityManifest", StringComparison.Ordinal) == true);

  /// <summary>An operator's redelivery request for whole streams.</summary>
  public async Task OperatorRedeliverAsync(IReadOnlyList<Guid> streams, string[] types) {
    await _originRedeliveryReceptor.HandleAsync(new RequestRedeliveryCommand {
      RuleFingerprint = ConsumerGuard.Stamp(types),
      RequesterService = CONSUMER_NAME,
      Topic = CONSUMER_TOPIC,
      StreamIds = streams,
      EventTypes = types,
      OperatorRequested = true,
    });
    await _pumpUntilQuietAsync();
  }

  private async Task _pumpUntilQuietAsync() {
    for (var guard = 0; guard < 200; guard++) {
      var progressed = false;
      while (_processedOrigin < _originTransport.Published.Count) {
        var (envelope, _, envelopeType) = _originTransport.Published[_processedOrigin++];
        progressed = true;
        if (envelopeType?.Contains("RedeliveryComposite", StringComparison.Ordinal) == true) {
          await _applyBundleAsync(envelope);
        } else if (envelopeType?.Contains(nameof(OriginPositionsAnswer), StringComparison.Ordinal) == true) {
          await _consumerPositionsReceptor.HandleAsync(_payload<OriginPositionsAnswer>(envelope));
        } else if (envelopeType?.Contains("IntegrityManifest", StringComparison.Ordinal) == true) {
          var manifest = _payload<IntegrityManifest>(envelope);
          await _consumerManifestReceptor.HandleAsync(OriginPredatesRuleFingerprint ? manifest with { RuleFingerprint = null } : manifest);
        }
      }
      while (_processedConsumer < _consumerTransport.Published.Count) {
        var (envelope, _, envelopeType) = _consumerTransport.Published[_processedConsumer++];
        progressed = true;
        if (envelopeType?.Contains("RequestIntegrityManifest", StringComparison.Ordinal) == true) {
          await _originRequestReceptor.HandleAsync(_payload<RequestIntegrityManifest>(envelope));
        } else if (envelopeType?.Contains("RequestRedeliveryCommand", StringComparison.Ordinal) == true) {
          await _originRedeliveryReceptor.HandleAsync(_payload<RequestRedeliveryCommand>(envelope));
        } else if (envelopeType?.Contains(nameof(RequestOriginPositions), StringComparison.Ordinal) == true) {
          await _originPositionsReceptor.HandleAsync(_payload<RequestOriginPositions>(envelope));
        }
      }
      if (!progressed) {
        return;
      }
    }
    throw new InvalidOperationException("the wire did not quiesce: the protocol is not converging");
  }

  /// <summary>A redelivery bundle reaches the consumer: expanded by the real composite fan-out, stored by the inbox.</summary>
  private async Task _applyBundleAsync(IMessageEnvelope envelope) {
    var bundle = _payload<RedeliveryComposite>(envelope);
    _redelivered.AddRange(bundle.InnerEventIds);
    using var scope = _consumerProvider!.CreateScope();
    var result = CompositeInboxFanout.TryExpand(bundle, envelope, scope.ServiceProvider);
    if (result.Outcome != CompositeInboxFanout.FanoutOutcome.Expanded) {
      throw new InvalidOperationException($"the redelivery bundle did not expand: {result.Outcome} {result.Detail}");
    }
    // As the inbox worker does once the children are committed: the bundle's answer is recorded and the redelivery
    // reconciled.
    var children = result.Children.Where(c => c.SourceServiceId == bundle.OriginServiceId).ToList();
    _restored.AddRange(await _storeOnConsumerAsync([.. result.Children], async () =>
      _ = await RedeliveryReconciler.ReconcileAsync(bundle, children, scope.ServiceProvider,
        NullLogger.Instance, _clock, RedeliveryReconciler.OriginServiceNameOf(envelope))));
  }

  private T _payload<T>(IMessageEnvelope envelope) =>
    (T)JsonSerializer.Deserialize(((MessageEnvelope<JsonElement>)envelope).Payload.GetRawText(), _json.GetTypeInfo(typeof(T)))!;

  /// <summary>What the wire has carried so far.</summary>
  public TwoServiceTally Tally() {
    var repairs = 0;
    foreach (var (_, _, type) in _consumerTransport.Published) {
      if (type?.Contains("RequestRedeliveryCommand", StringComparison.Ordinal) == true) {
        repairs++;
      }
    }
    var reports = _consumerDispatcher.Published.OfType<IntegrityDivergenceDetected>().Count();
    return new TwoServiceTally(repairs, reports, _redelivered.Count, _restored.Count);
  }

  /// <summary>The redelivery requests the consumer has sent so far (#2365).</summary>
  public List<RequestRedeliveryCommand> RedeliveryRequests() => [.. _consumerTransport.Published
    .Where(p => p.EnvelopeType?.Contains("RequestRedeliveryCommand", StringComparison.Ordinal) == true)
    .Select(p => _payload<RequestRedeliveryCommand>(p.Envelope))];

  /// <summary>The redelivery bundles the origin has sent so far (#2365).</summary>
  public List<RedeliveryComposite> RedeliveryBundles() => [.. _originTransport.Published
    .Where(p => p.EnvelopeType?.Contains("RedeliveryComposite", StringComparison.Ordinal) == true)
    .Select(p => _payload<RedeliveryComposite>(p.Envelope))];

  /// <summary>The events redelivered so far, in order.</summary>
  public IReadOnlyList<Guid> Redelivered => _redelivered;

  public async Task<bool> ConsumerHoldsAsync(Guid eventId) {
    await using var conn = await _openAsync(Consumer);
    return await _scalarAsync(conn, "SELECT COUNT(*) FROM wh_event_store WHERE event_id = @id", ("id", eventId)) == 1;
  }

  /// <summary>A count read from a store.</summary>
  public static async Task<long> CountAsync(TwoServiceStore store, string sql, params (string Name, object Value)[] parameters) {
    await using var conn = await _openAsync(store);
    return await _scalarAsync(conn, sql, parameters);
  }

  /// <summary>A statement run on a store for its effect.</summary>
  public static async Task ExecuteAsync(TwoServiceStore store, string sql, params (string Name, object Value)[] parameters) {
    await using var conn = await _openAsync(store);
    await _executeAsync(conn, sql, parameters);
  }

  /// <summary>Every event a store holds of the streams, as id:version, ordered by id.</summary>
  public static async Task<string> FingerprintAsync(TwoServiceStore store, IReadOnlyList<Guid> streams) {
    await using var conn = await _openAsync(store);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT COALESCE(string_agg(es.event_id || ':' || es.version, ',' ORDER BY es.event_id), '')
      FROM wh_event_store es WHERE es.stream_id = ANY(@s)
      """;
    cmd.Parameters.AddWithValue("s", streams.ToArray());
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  /// <summary>Each event a store holds of the stream: id, version, flags, key and the origin's sequence, for a diagnosis.</summary>
  public static async Task<string> DescribeAsync(TwoServiceStore store, Guid stream) {
    await using var conn = await _openAsync(store);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT COALESCE(string_agg(format('%s v%s flags %s key %s oseq %s', event_id, version, flags, supersede_key,
        origin_commit_sequence), '; ' ORDER BY version), '')
      FROM wh_event_store WHERE stream_id = @s
      """;
    cmd.Parameters.AddWithValue("s", stream);
    return (string)(await cmd.ExecuteScalarAsync())!;
  }

  private async Task<Guid> _localServiceIdAsync(TwoServiceStore store) {
    await using var ctx = new WorkCoordinationDbContext(store.Options);
    return await new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, _json).GetLocalServiceIdAsync();
  }

  private ServiceProvider _buildProvider(
      TwoServiceStore store, IntegrityWireTransport transport, IntegrityWireDispatcher dispatcher, string serviceName,
      StreamIntegrityOptions options, IntegrityGapTracker? tracker,
      IReadOnlyList<string>? consumedEventTypes = null, TimeProvider? clock = null, IRuleFingerprintSource? rules = null,
      StreamIntegrityMetrics? metrics = null) {
    var services = new ServiceCollection();
    if (metrics is not null) {
      services.AddSingleton(metrics);
    }
    // #2362: each side's rules and its own pair guard, as the turnkey registration gives every service.
    services.AddSingleton(rules ?? CatalogRuleFingerprintSource.Empty);
    services.AddSingleton(sp => new IntegrityPairGuard(sp.GetRequiredService<IRuleFingerprintSource>(), Options.Create(options),
      NullLogger<IntegrityPairGuard>.Instance, sp.GetService<TimeProvider>()));
    // #2544: the maintenance-cycle integrity work reaches each side's own database the way the driver registers it.
    services.AddSingleton(new PostgresArchiveConnection(async ct => {
      var connection = new NpgsqlConnection(store.ConnectionString);
      await connection.OpenAsync(ct);
      return connection;
    }, "public"));
    services.AddScoped<IWorkCoordinator>(_ =>
      new EFCoreWorkCoordinator<WorkCoordinationDbContext>(new WorkCoordinationDbContext(store.Options), _json));
    services.AddSingleton<ITransport>(transport);
    services.AddSingleton<IDispatcher>(dispatcher);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(_json));
    services.AddSingleton<IServiceInstanceProvider>(new IntegrityWireInstance(serviceName));
    services.AddSingleton(Options.Create(options));
    // The durable, coordinator-backed ledger the turnkey registration uses.
    services.AddSingleton<IIntegrityRepairLedger>(sp =>
      new CoordinatorIntegrityRepairLedger(sp.GetRequiredService<IServiceScopeFactory>()));
    services.AddSingleton<IConsumerRegistry>(new Whizbang.Testing.Workers.PermissiveReceptorRegistryQuery());
    if (consumedEventTypes is not null) {
      services.AddSingleton<IConsumerRegistry>(new ConsumedTypes(consumedEventTypes));
    }
    if (clock is not null) {
      services.AddSingleton(clock);
    }
    // The consumer's catalog: what the fan-out derives a redelivered child's flags from, by its type name.
    services.AddSingleton<IEventMarkerResolver>(new Markers(_markers));
    if (tracker is not null) {
      services.AddSingleton(tracker);
    }
    var consumerOptions = new TransportConsumerOptions();
    consumerOptions.Destinations.Add(new TransportDestination(CONSUMER_TOPIC));
    services.AddSingleton(consumerOptions);
    return services.BuildServiceProvider();
  }

  private static async Task<NpgsqlConnection> _openAsync(TwoServiceStore store) {
    var conn = new NpgsqlConnection(store.ConnectionString);
    await conn.OpenAsync();
    return conn;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    _ = await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _scalarAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in parameters) {
      cmd.Parameters.AddWithValue(name, value);
    }
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
  }

  /// <summary>A consumer that consumes exactly the given event types, and keeps everything it receives.</summary>
  private sealed class ConsumedTypes(IReadOnlyList<string> types) : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => true;

    public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() => [.. types.Select(t => new ConsumedMessageInfo(t, IsEvent: true))];
  }

  /// <summary>The flags the consumer's catalog declares, by CLR name, as a generated catalog would answer.</summary>
  private sealed class Markers(Dictionary<string, EventFlags> byName) : IEventMarkerResolver {
    public EventFlags? Resolve(string clrTypeName) => byName.TryGetValue(clrTypeName, out var flags) ? flags : null;

    public EventFlags? Resolve(Type type) => null;
  }
}
