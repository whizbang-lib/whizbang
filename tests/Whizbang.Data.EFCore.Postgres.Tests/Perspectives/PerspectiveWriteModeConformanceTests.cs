// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
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
using Whizbang.Core.Notifications;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.Signals;
using Whizbang.Core.Tracing;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Options;
using Whizbang.Testing.Workers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The write-mode conformance suite (#2246): one contract, run against every write mode. Each case drives real
/// generated runners through the real pipeline on PostgreSQL (the claim loop, the <see cref="PerspectiveWorker"/>, the
/// driver's state source and sinks), and asserts what the read model must hold whatever the mode. A mode is supported
/// only when it passes the whole suite.
/// </summary>
/// <remarks>
/// <para>
/// Concurrent writers are driven deterministically through the flush-policy seam: a test policy runs between the
/// batch's prepare and its write and changes the row in the database, as another writer would. Under the per-stream
/// mode no batch write happens, so nothing is injected, and each case's expectation counts exactly the writes that
/// happened. Every wait is on the completion or failure of the work rows it asserts, never on time.
/// </para>
/// <para>
/// Every stream is committed and handed to the worker's queues before the worker starts, so one worker batch holds the
/// case's streams.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes#conformance-suite</docs>
[Category("Integration")]
[Category("Shard5")]
public class PerspectiveWriteModeConformanceTests : EFCoreTestBase {
  private const string LEDGER = "wh_per_conformance_ledger";
  private const string SPLIT_LEDGER = "wh_per_conformance_split_ledger";
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(60);

  // ---------------------------------------------------------------------------------------------------------------

  [Test]
  [Arguments(PerspectiveWriteMode.SetBased)]
  [Arguments(PerspectiveWriteMode.PerStream)]
  public async Task NewAndExistingStreams_FoldEveryEventOnce_IntoTheDocumentAndBothPhysicalFieldFormsAsync(PerspectiveWriteMode mode) {
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    var existing = _streams(3);
    var fresh = _streams(3);
    ServiceInstanceProvider instance;
    await using (var first = _compose(mode)) {
      instance = first.InstanceProvider;
      await first.RegisterAsync(conn, ct);
      var work = new List<Guid>();
      foreach (var stream in existing) {
        work.AddRange(await first.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 10, Owner = "owner-a" }, ct));
      }
      await first.RunAsync(existing, work, ct);
    }

    await using var pipeline = _compose(mode, instance);
    var second = new List<Guid>();
    foreach (var stream in existing) {
      second.AddRange(await pipeline.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 5, Owner = "owner-b" }, ct));
    }
    foreach (var stream in fresh) {
      second.AddRange(await pipeline.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 7, Owner = "owner-c" }, ct));
    }
    await pipeline.RunAsync([.. existing, .. fresh], second, ct);

    foreach (var stream in existing) {
      await _assertLedgerAsync(conn, stream, balance: 15, postings: 2, owner: "owner-b", ct);
    }
    foreach (var stream in fresh) {
      await _assertLedgerAsync(conn, stream, balance: 7, postings: 1, owner: "owner-c", ct);
    }
  }

  [Test]
  [Arguments(PerspectiveWriteMode.SetBased)]
  [Arguments(PerspectiveWriteMode.PerStream)]
  public async Task AStreamWhoseFirstEventFindsARowAlready_FoldsOntoThatRowAsync(PerspectiveWriteMode mode) {
    // A stream known to be new can still have a row (one written by something other than its own events): the batch
    // inserts only where no row is, and the conflict reruns the pair onto the row it finds.
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    var stream = _streams(1)[0];
    await using (var context = CreateDbContext()) {
      var store = new EFCorePostgresPerspectiveStore<ConformanceLedger>(context, LEDGER);
      await store.UpsertAsync(stream, new ConformanceLedger { Id = stream, Balance = 100, Postings = 1, Owner = "seed" });
    }
    await using var pipeline = _compose(mode);
    await pipeline.RegisterAsync(conn, ct);
    var work = await pipeline.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 10, Owner = "owner-a" }, ct);

    await pipeline.RunAsync([stream], work, ct);

    await _assertLedgerAsync(conn, stream, balance: 110, postings: 2, owner: "owner-a", ct);
  }

  [Test]
  [Arguments(PerspectiveWriteMode.SetBased)]
  [Arguments(PerspectiveWriteMode.PerStream)]
  public async Task ARowAnotherWriterChangesAfterTheRead_KeepsBothWritesAsync(PerspectiveWriteMode mode) {
    // The row version conflict: another writer (a collective, another instance) changes the row between the batch's read
    // and its write. Neither write may be lost.
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    var stream = _streams(1)[0];
    ServiceInstanceProvider instance;
    await using (var first = _compose(mode)) {
      instance = first.InstanceProvider;
      await first.RegisterAsync(conn, ct);
      await first.RunAsync([stream], await first.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 10, Owner = "a" }, ct), ct);
    }
    await using var pipeline = _compose(mode, instance);
    var injected = false;
    pipeline.Policy.BeforeFlush = async writes => {
      if (writes.Any(w => w.StreamId == stream && w.Table.Contains(LEDGER, StringComparison.Ordinal) && !w.Table.Contains("split", StringComparison.Ordinal))) {
        await _executeAsync(
          $"UPDATE {LEDGER} SET data = jsonb_set(data, '{{Balance}}', to_jsonb((data->>'Balance')::numeric + 1000)) WHERE id = @id", stream);
        injected = true;
      }
    };
    var work = await pipeline.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 5, Owner = "a" }, ct);

    await pipeline.RunAsync([stream], work, ct);

    await Assert.That(injected).IsEqualTo(mode == PerspectiveWriteMode.SetBased)
      .Because("the flush policy runs between a set-based batch's prepare and its write, and only there");
    await _assertLedgerAsync(conn, stream, balance: 15 + (injected ? 1000 : 0), postings: 2, owner: "a", ct);
  }

  [Test]
  [Arguments(PerspectiveWriteMode.SetBased)]
  [Arguments(PerspectiveWriteMode.PerStream)]
  public async Task ARowAnotherWriterMovesPastTheBatch_IsRefoldedWithEveryEvent_AndTheLaterDeliveryIsNotAppliedTwiceAsync(PerspectiveWriteMode mode) {
    // The ordering guard and the rewind: between the batch's read and its write another worker commits a later event on
    // the stream and folds it into the row first. The batch's event now sits below the row's position, so the stream is
    // re-folded with every event, and the later event's own delivery, which the row already holds, is not applied again.
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    var stream = _streams(1)[0];
    ServiceInstanceProvider instance;
    await using (var first = _compose(mode)) {
      instance = first.InstanceProvider;
      await first.RegisterAsync(conn, ct);
      await first.RunAsync([stream], await first.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 10, Owner = "a" }, ct), ct);
    }
    await using var pipeline = _compose(mode, instance);
    var later = new List<Guid>();
    pipeline.Policy.BeforeFlush = async writes => {
      if (later.Count == 0 && writes.Any(w => w.StreamId == stream)) {
        later.AddRange(await pipeline.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 100, Owner = "a" }, ct));
        var laterEvent = await _scalarAsync<Guid>(conn,
          "SELECT event_id FROM wh_perspective_events WHERE event_work_id = @id", later[0], ct);
        var laterSequence = await _scalarAsync<long>(conn, "SELECT commit_sequence FROM wh_event_store WHERE event_id = @id", laterEvent, ct);
        await _executeAsync(
          $$"""
          UPDATE {{LEDGER}} SET
            data = jsonb_set(jsonb_set(data, '{Balance}', to_jsonb((data->>'Balance')::numeric + 100)), '{Postings}', to_jsonb((data->>'Postings')::int + 1)),
            metadata = jsonb_set(jsonb_set(metadata, '{EventId}', to_jsonb('{{laterEvent}}'::text)), '{CommitSequence}', to_jsonb({{laterSequence}}))
          WHERE id = @id
          """, stream);
      }
    };
    var work = await pipeline.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 5, Owner = "a" }, ct);

    await pipeline.RunAsync([stream], work, ct);
    await pipeline.Completion.WaitForAllAsync(later);

    var moved = later.Count > 0;
    await Assert.That(moved).IsEqualTo(mode == PerspectiveWriteMode.SetBased);
    await _assertLedgerAsync(conn, stream, balance: 15 + (moved ? 100 : 0), postings: moved ? 3 : 2, owner: "a", ct);
  }

  [Test]
  [Arguments(PerspectiveWriteMode.SetBased)]
  [Arguments(PerspectiveWriteMode.PerStream)]
  public async Task APurgedStream_LosesItsRow_AndStaysPurgedAsync(PerspectiveWriteMode mode) {
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    var purged = _streams(1)[0];
    var kept = _streams(1)[0];
    ServiceInstanceProvider instance;
    await using (var first = _compose(mode)) {
      instance = first.InstanceProvider;
      await first.RegisterAsync(conn, ct);
      var work = new List<Guid>();
      work.AddRange(await first.CommitAsync(conn, new LedgerPosted { LedgerId = purged, Amount = 10, Owner = "a" }, ct));
      work.AddRange(await first.CommitAsync(conn, new LedgerPosted { LedgerId = kept, Amount = 10, Owner = "a" }, ct));
      await first.RunAsync([purged, kept], work, ct);
    }
    await using (var second = _compose(mode, instance)) {
      await second.RunAsync([purged], await second.CommitAsync(conn, new LedgerClosed { LedgerId = purged }, ct), ct);
    }
    await Assert.That(await _ledgerExistsAsync(conn, purged, ct)).IsFalse();

    await using var third = _compose(mode, instance);
    var work3 = new List<Guid>();
    work3.AddRange(await third.CommitAsync(conn, new LedgerPosted { LedgerId = purged, Amount = 5, Owner = "a" }, ct));
    work3.AddRange(await third.CommitAsync(conn, new LedgerPosted { LedgerId = kept, Amount = 5, Owner = "a" }, ct));
    await third.RunAsync([purged, kept], work3, ct);

    await Assert.That(await _ledgerExistsAsync(conn, purged, ct)).IsFalse()
      .Because("purge stays purged: an event after the purge is not applied to an empty model");
    await _assertLedgerAsync(conn, kept, balance: 15, postings: 2, owner: "a", ct);
  }

  [Test]
  [Arguments(PerspectiveWriteMode.SetBased)]
  [Arguments(PerspectiveWriteMode.PerStream)]
  public async Task AnApplyThatThrows_SkipsItsEvent_AndARowTheDatabaseRejects_FailsOnlyItsOwnStreamAsync(PerspectiveWriteMode mode) {
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    await using (var constraint = conn.CreateCommand()) {
      constraint.CommandText = $"ALTER TABLE {LEDGER} ADD CONSTRAINT ck_conformance_rejects_13 CHECK ((data ->> 'Balance')::numeric <> 13)";
      await constraint.ExecuteNonQueryAsync(ct);
    }
    var healthy = _streams(1)[0];
    var throwing = _streams(1)[0];
    var rejected = _streams(1)[0];
    await using var pipeline = _compose(mode);
    await pipeline.RegisterAsync(conn, ct);
    var healthyWork = await pipeline.CommitAsync(conn, new LedgerPosted { LedgerId = healthy, Amount = 10, Owner = "a" }, ct);
    var throwingWork = await pipeline.CommitAsync(conn, new LedgerRejected { LedgerId = throwing }, ct);
    var rejectedWork = await pipeline.CommitAsync(conn, new LedgerPosted { LedgerId = rejected, Amount = 13, Owner = "a" }, ct);
    var rejectedLedgerWork = await _workOfAsync(conn, rejectedWork, "ConformanceLedgerPerspective", ct);

    await pipeline.HandOffAsync([healthy, throwing, rejected], ct);
    await pipeline.StartWorkerAsync(ct);
    // An Apply that throws is logged and its event skipped, as the runner has always treated it: the stream's work
    // completes and the cursor moves past the event, so it is not retried forever.
    await pipeline.Completion.WaitForAllAsync([.. healthyWork, .. throwingWork, .. rejectedWork.Except([rejectedLedgerWork])]);
    var rowFailure = await pipeline.Failures.WaitForAsync(rejectedLedgerWork);

    await Assert.That(await _ledgerExistsAsync(conn, throwing, ct)).IsFalse()
      .Because("the only event of the stream was skipped, so there is nothing to write");
    await Assert.That(rowFailure.Error).Contains("ck_conformance_rejects_13");
    await _assertLedgerAsync(conn, healthy, balance: 10, postings: 1, owner: "a", ct);
    await Assert.That(await _ledgerExistsAsync(conn, rejected, ct)).IsFalse();
    await Assert.That(await _splitBalanceAsync(conn, rejected, ct)).IsEqualTo(13m)
      .Because("the other perspective of the rejected stream has no such constraint, and its row is written");
  }

  [Test]
  [Arguments(PerspectiveWriteMode.SetBased)]
  [Arguments(PerspectiveWriteMode.PerStream)]
  public async Task ASyncWaiter_IsReleasedOnlyOnceTheRowIsDurableAsync(PerspectiveWriteMode mode) {
    var ct = CancellationToken.None;
    await using var conn = await _openAsync(ct);
    var stream = _streams(1)[0];
    await using var pipeline = _compose(mode);
    await pipeline.RegisterAsync(conn, ct);
    var work = await pipeline.CommitAsync(conn, new LedgerPosted { LedgerId = stream, Amount = 10, Owner = "a" }, ct);
    var eventId = await _scalarAsync<Guid>(conn, "SELECT event_id FROM wh_perspective_events WHERE event_work_id = @id", work[0], ct);
    var perspective = TypeNameFormatter.GetPerspectiveName(typeof(ConformanceLedgerPerspective));
    pipeline.Sync.TrackEvent(typeof(LedgerPosted), eventId, stream, perspective);

    await pipeline.HandOffAsync([stream], ct);
    await pipeline.StartWorkerAsync(ct);
    var released = await pipeline.Sync.WaitForPerspectiveEventsAsync([eventId], perspective, _signalDeadline, cancellationToken: ct);

    await Assert.That(released).IsTrue();
    await _assertLedgerAsync(conn, stream, balance: 10, postings: 1, owner: "a", ct);
    await pipeline.Completion.WaitForAllAsync(work);
  }

  // ===== Harness =====

  private static List<Guid> _streams(int count) => [.. Enumerable.Range(0, count).Select(_ => (Guid)TrackedGuid.New())];

  private static async Task _assertLedgerAsync(NpgsqlConnection conn, Guid stream, decimal balance, int postings, string owner, CancellationToken ct) {
    await using var command = conn.CreateCommand();
    command.CommandText = $"SELECT (data->>'Balance')::numeric, (data->>'Postings')::int, data->>'Owner', owner FROM {LEDGER} WHERE id = @id";
    command.Parameters.AddWithValue("id", stream);
    await using (var reader = await command.ExecuteReaderAsync(ct)) {
      await Assert.That(await reader.ReadAsync(ct)).IsTrue().Because($"ledger {stream} has a row");
      await Assert.That(reader.GetDecimal(0)).IsEqualTo(balance).Because("every event is folded once: none lost, none twice");
      await Assert.That(reader.GetInt32(1)).IsEqualTo(postings);
      await Assert.That(reader.GetString(2)).IsEqualTo(owner).Because("an Extracted field is in the document");
      await Assert.That(reader.GetString(3)).IsEqualTo(owner).Because("and in its column");
    }
    await using var split = conn.CreateCommand();
    split.CommandText = $"SELECT (data->>'Balance')::numeric, jsonb_exists(data, 'Owner'), owner FROM {SPLIT_LEDGER} WHERE id = @id";
    split.Parameters.AddWithValue("id", stream);
    await using var splitReader = await split.ExecuteReaderAsync(ct);
    if (await splitReader.ReadAsync(ct)) {
      await Assert.That(splitReader.GetBoolean(1)).IsFalse().Because("a Split field is not in the document");
      await Assert.That(splitReader.GetString(2)).IsEqualTo(owner).Because("a Split field is in its column");
    }
  }

  private static async Task<bool> _ledgerExistsAsync(NpgsqlConnection conn, Guid stream, CancellationToken ct) =>
    await _scalarAsync<bool>(conn, $"SELECT EXISTS (SELECT 1 FROM {LEDGER} WHERE id = @id)", stream, ct);

  private static async Task<decimal> _splitBalanceAsync(NpgsqlConnection conn, Guid stream, CancellationToken ct) =>
    await _scalarAsync<decimal>(conn, $"SELECT (data->>'Balance')::numeric FROM {SPLIT_LEDGER} WHERE id = @id", stream, ct);

  private static async Task<Guid> _workOfAsync(NpgsqlConnection conn, IReadOnlyList<Guid> work, string perspectiveSimpleName, CancellationToken ct) {
    foreach (var id in work) {
      var name = await _scalarAsync<string>(conn, "SELECT perspective_name FROM wh_perspective_events WHERE event_work_id = @id", id, ct);
      if (name.EndsWith(perspectiveSimpleName, StringComparison.Ordinal)) {
        return id;
      }
    }
    throw new InvalidOperationException($"No work row of {perspectiveSimpleName}.");
  }

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private async Task _executeAsync(string sql, Guid id) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var command = new NpgsqlCommand(sql, conn);
    command.Parameters.AddWithValue(nameof(id), id);
    await command.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, Guid id, CancellationToken ct) {
    await using var command = conn.CreateCommand();
    command.CommandText = sql;
    command.Parameters.AddWithValue(nameof(id), id);
    return (T)(await command.ExecuteScalarAsync(ct))!;
  }

  private Pipeline _compose(PerspectiveWriteMode mode, ServiceInstanceProvider? instance = null) {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var policy = new InjectingFlushPolicy();
    var sync = new SyncEventTracker();

    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddLogging();
    services.AddScoped(_ => CreateDbContext());
    services.AddScoped<IWorkCoordinator>(sp => new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddScoped<IEventStore>(sp => new EFCoreEventStore<WorkCoordinationDbContext>(
      sp.GetRequiredService<WorkCoordinationDbContext>(), jsonOptions));
    services.AddSingleton<IScopeContextAccessor>(new ScopeContextAccessor());
    services.AddOptions<Whizbang.Core.Configuration.WhizbangCoreOptions>();
    services.AddPerspectiveRunners();
    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services, typeof(WorkCoordinationDbContext), typeof(ConformanceLedger), LEDGER, new PostgresUpsertStrategy());
    EFCoreInfrastructureRegistration.RegisterPerspectiveModel(
      services, typeof(WorkCoordinationDbContext), typeof(ConformanceSplitLedger), SPLIT_LEDGER, new PostgresUpsertStrategy());
    // The seams the Postgres driver registers for the service's context, and the test's flush policy.
    services.AddSingleton<IPerspectiveApplyCoordinator>(new PerspectiveApplyCoordinator(NullLogger<PerspectiveApplyCoordinator>.Instance));
    services.AddSingleton<IPerspectiveStateSource>(new BatchedPerspectiveStateSource());
    services.AddSingleton<IApplyBatchStrategy>(policy);
    services.AddSingleton<IPerspectivePurgeMarkerStore>(new PostgresPerspectivePurgeMarkerStore(dataSource.OpenConnectionAsync));
    services.AddKeyedScoped<IPerspectiveWriteSink>(typeof(WorkCoordinationDbContext), (sp, _) =>
      new EFCorePostgresPerspectiveWriteSink(sp.GetRequiredService<WorkCoordinationDbContext>()));
    services.AddMetrics();
    var provider = services.BuildServiceProvider();
    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

    var instanceProvider = instance ?? new ServiceInstanceProvider(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    var harness = new PerspectiveWorkerTestHarness();
    var handedOff = new HandedOffStreams();
    var failures = new RecordingFailureChannel();
    var completion = new SignalingCompletionChannel(scopeFactory, failures);

    var claimWorker = new ClaimWorker(
      scopeFactory: scopeFactory,
      instanceProvider: instanceProvider,
      notificationListener: new NoOpWorkNotificationListener(),
      options: Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 50,
        PollingMaxIntervalMilliseconds = 200,
        AdaptiveClaimWindow = false,
        AdaptiveOutstandingBudget = false,
        MaxStreamsPerBatch = 100,
      }),
      logger: NullLogger<ClaimWorker>.Instance,
      outboxChannel: new WorkChannelWriter(),
      inboxChannel: new InboxChannelWriter(),
      perspectiveChannel: new CountingWorkChannel(harness.ChannelWriter, handedOff),
      perspectiveDrainChannel: new CountingDrainChannel(harness.DrainChannel, handedOff),
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: new InboxDrainChannel(),
      signalingGate: NullNotifySignalingGate.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      signalBus: NullSignalBus.Instance);

    var workerOptions = new PerspectiveWorkerOptions { MaxConcurrentDrainConsumers = 1, WriteMode = mode };
    var perspectiveWorker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: scopeFactory,
      options: Options.Create(workerOptions),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: provider.GetRequiredService<IEventTypeProvider>(),
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: sync,
      logger: NullLogger<PerspectiveWorker>.Instance,
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: completion,
      failureChannel: failures,
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: NullDeadLetterStore.Instance,
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor(workerOptions));

    return new Pipeline {
      Services = provider,
      DataSource = dataSource,
      InstanceProvider = instanceProvider,
      ClaimWorker = claimWorker,
      PerspectiveWorker = perspectiveWorker,
      Completion = completion,
      Failures = failures,
      HandedOff = handedOff,
      Policy = policy,
      Sync = sync,
    };
  }

  private sealed class Pipeline : IAsyncDisposable {
    public required ServiceProvider Services { get; init; }
    public required NpgsqlDataSource DataSource { get; init; }
    public required ServiceInstanceProvider InstanceProvider { get; init; }
    public required ClaimWorker ClaimWorker { get; init; }
    public required PerspectiveWorker PerspectiveWorker { get; init; }
    public required SignalingCompletionChannel Completion { get; init; }
    public required RecordingFailureChannel Failures { get; init; }
    public required HandedOffStreams HandedOff { get; init; }
    public required InjectingFlushPolicy Policy { get; init; }
    public required SyncEventTracker Sync { get; init; }
    private bool _claiming;
    private bool _working;

    /// <summary>Registers this instance and the ledgers' associations with their event types.</summary>
    public async Task RegisterAsync(NpgsqlConnection conn, CancellationToken ct) {
      await using (var reg = conn.CreateCommand()) {
        reg.CommandText = """
          INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
          VALUES (@id, 'conformance-svc', 'conformance-host', 1, NOW(), NOW(), '{}'::jsonb)
          ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()
          """;
        reg.Parameters.AddWithValue("id", InstanceProvider.InstanceId);
        await reg.ExecuteNonQueryAsync(ct);
      }
      (Type Event, Type Perspective)[] associations = [
        (typeof(LedgerPosted), typeof(ConformanceLedgerPerspective)),
        (typeof(LedgerClosed), typeof(ConformanceLedgerPerspective)),
        (typeof(LedgerRejected), typeof(ConformanceLedgerPerspective)),
        (typeof(LedgerPosted), typeof(ConformanceSplitLedgerPerspective)),
      ];
      foreach (var (eventType, perspective) in associations) {
        var name = TypeNameFormatter.Format(eventType);
        await using var assoc = conn.CreateCommand();
        assoc.CommandText = """
          INSERT INTO wh_message_associations
            (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
          VALUES (gen_random_uuid(), @eventType, 'perspective', @target, 'conformance-svc', @eventType, NOW(), NOW())
          ON CONFLICT DO NOTHING
          """;
        assoc.Parameters.AddWithValue("eventType", name);
        assoc.Parameters.AddWithValue("target", TypeNameFormatter.GetPerspectiveName(perspective));
        await assoc.ExecuteNonQueryAsync(ct);
      }
    }

    /// <summary>Hands every stream to the worker's queues, starts the worker, and waits for every work row.</summary>
    public async Task RunAsync(IReadOnlyCollection<Guid> streams, IReadOnlyList<Guid> work, CancellationToken ct) {
      await HandOffAsync(streams, ct);
      await StartWorkerAsync(ct);
      await Completion.WaitForAllAsync(work);
    }

    /// <summary>Starts the claim loop and waits until it has handed every one of the streams to the worker's queues.</summary>
    public async Task HandOffAsync(IReadOnlyCollection<Guid> streams, CancellationToken ct) {
      var handed = HandedOff.WaitForAsync(streams);
      _claiming = true;
      await ClaimWorker.StartAsync(ct);
      try {
        await handed.WaitAsync(_signalDeadline, ct);
      } catch (TimeoutException) {
        throw new TimeoutException($"Streams not handed to the worker: {string.Join(",", streams.Where(s => !HandedOff.Has(s)))}");
      }
    }

    /// <summary>Starts the worker, whose first batch is everything queued.</summary>
    public async Task StartWorkerAsync(CancellationToken ct) {
      _working = true;
      await PerspectiveWorker.StartAsync(ct);
      try {
        await PerspectiveWorker.StartupScanComplete.WaitAsync(_signalDeadline, ct);
      } catch (TimeoutException) {
        throw new TimeoutException("The worker's startup scan did not complete");
      }
    }

    /// <summary>Commits one event and stamps it as the commit-order stamper would; returns its work rows.</summary>
    public async Task<List<Guid>> CommitAsync<TEvent>(NpgsqlConnection conn, TEvent payload, CancellationToken ct) where TEvent : IEvent {
      var eventId = (Guid)TrackedGuid.New();
      var streamId = payload switch {
        LedgerPosted posted => posted.LedgerId,
        LedgerClosed closed => closed.LedgerId,
        LedgerRejected rejected => rejected.LedgerId,
        _ => throw new ArgumentOutOfRangeException(nameof(payload)),
      };
      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = MessageId.From(eventId),
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        Hops = [],
        Payload = JsonSerializer.SerializeToElement(payload, jsonOptions.GetTypeInfo(typeof(TEvent))),
      };
      var envelopeJson = JsonSerializer.Serialize(envelope, jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
      var request = $$"""
        {
          "instance_id": "{{InstanceProvider.InstanceId}}",
          "service_name": "{{InstanceProvider.ServiceName}}",
          "host_name": "{{InstanceProvider.HostName}}",
          "process_id": {{InstanceProvider.ProcessId}},
          "new_outbox_messages": [{
            "MessageId": "{{eventId}}",
            "Destination": "ledger-events",
            "MessageType": "{{TypeNameFormatter.Format(typeof(TEvent))}}",
            "EnvelopeType": null,
            "Envelope": {{envelopeJson}},
            "Metadata": {},
            "Scope": null,
            "StreamId": "{{streamId}}",
            "IsEvent": true
          }]
        }
        """;
      await using var own = await DataSource.OpenConnectionAsync(ct);
      await using (var call = own.CreateCommand()) {
        call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
        call.Parameters.AddWithValue("req", request);
        _ = await call.ExecuteScalarAsync(ct);
      }
      await using (var stamp = own.CreateCommand()) {
        stamp.CommandText = "SELECT stamp_pending_commit_sequences(10000, false)";
        _ = await stamp.ExecuteScalarAsync(ct);
      }
      var work = new List<Guid>();
      await using var select = own.CreateCommand();
      select.CommandText = "SELECT event_work_id FROM wh_perspective_events WHERE event_id = @id";
      select.Parameters.AddWithValue("id", eventId);
      await using var reader = await select.ExecuteReaderAsync(ct);
      while (await reader.ReadAsync(ct)) {
        work.Add(reader.GetGuid(0));
      }
      _ = conn;
      return work;
    }

    public async ValueTask DisposeAsync() {
      if (_working) {
        await PerspectiveWorker.StopAsync(CancellationToken.None);
      }
      if (_claiming) {
        await ClaimWorker.StopAsync(CancellationToken.None);
      }
      await Services.DisposeAsync();
      await DataSource.DisposeAsync();
    }
  }

  /// <summary>
  /// The flush policy of the suite: runs <see cref="BeforeFlush"/> between a batch's prepare and its write, as another
  /// writer acting in that window would, then writes the batch.
  /// </summary>
  private sealed class InjectingFlushPolicy : IApplyBatchStrategy {
    public Func<IReadOnlyList<PendingPerspectiveWrite>, Task>? BeforeFlush { get; set; }

    public async Task<IReadOnlyList<PerspectiveWriteOutcome>> FlushAsync(
        IPerspectiveWriteSink sink, IReadOnlyList<PendingPerspectiveWrite> writes, CancellationToken cancellationToken = default) {
      if (BeforeFlush is { } before) {
        await before(writes);
      }
      return await sink.WriteAsync(writes, cancellationToken);
    }
  }

  /// <summary>Records the streams the claim loop handed to the worker's queues, and signals when a set of them is in.</summary>
  private sealed class HandedOffStreams {
    private readonly Lock _lock = new();
    private readonly HashSet<Guid> _handed = [];
    private readonly List<(HashSet<Guid> Streams, TaskCompletionSource Done)> _waits = [];

    public Task WaitForAsync(IEnumerable<Guid> streams) {
      lock (_lock) {
        var wanted = streams.ToHashSet();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (wanted.IsSubsetOf(_handed)) {
          done.SetResult();
        } else {
          _waits.Add((wanted, done));
        }
        return done.Task;
      }
    }

    public bool Has(Guid streamId) {
      lock (_lock) {
        return _handed.Contains(streamId);
      }
    }

    public void Add(Guid streamId) {
      List<TaskCompletionSource> ready;
      lock (_lock) {
        _handed.Add(streamId);
        ready = [.. _waits.Where(w => w.Streams.IsSubsetOf(_handed)).Select(w => w.Done)];
        _waits.RemoveAll(w => ready.Contains(w.Done));
      }
      foreach (var done in ready) {
        done.TrySetResult();
      }
    }
  }

  private sealed class CountingWorkChannel(IPerspectiveChannelWriter inner, HandedOffStreams handed) : IPerspectiveChannelWriter {
    public System.Threading.Channels.ChannelReader<PerspectiveWork> Reader => inner.Reader;

    public async ValueTask WriteAsync(PerspectiveWork work, CancellationToken ct = default) {
      await inner.WriteAsync(work, ct);
      handed.Add(work.StreamId);
    }

    public bool TryWrite(PerspectiveWork work) {
      var written = inner.TryWrite(work);
      if (written) {
        handed.Add(work.StreamId);
      }
      return written;
    }

    public void Complete() => inner.Complete();
  }

  private sealed class CountingDrainChannel(IPerspectiveDrainChannel inner, HandedOffStreams handed) : IPerspectiveDrainChannel {
    public System.Threading.Channels.ChannelReader<Guid> Reader => inner.Reader;

    public async ValueTask WriteAsync(Guid streamId, CancellationToken cancellationToken = default) {
      await inner.WriteAsync(streamId, cancellationToken);
      handed.Add(streamId);
    }

    public bool TryWrite(Guid streamId) {
      var written = inner.TryWrite(streamId);
      if (written) {
        handed.Add(streamId);
      }
      return written;
    }

    public bool IsInFlight(Guid streamId) => inner.IsInFlight(streamId);
    public void MarkDraining(Guid streamId) => inner.MarkDraining(streamId);
    public void MarkDrained(Guid streamId) => inner.MarkDrained(streamId);
  }

  /// <summary>Completes each row and cursor at the coordinator before returning, then signals each completed row.</summary>
  private sealed class SignalingCompletionChannel(IServiceScopeFactory scopeFactory, RecordingFailureChannel failures) : IPerspectiveCompletionChannel {
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _completed = new();

    /// <summary>Waits for every row; a timeout names the rows still open and every failure recorded meanwhile.</summary>
    public async Task WaitForAllAsync(IEnumerable<Guid> workIds) {
      var ids = workIds.ToList();
      try {
        await Task.WhenAll(ids.Select(id => _signal(id).Task)).WaitAsync(_signalDeadline);
      } catch (TimeoutException) {
        var open = ids.Where(id => !_signal(id).Task.IsCompleted).ToList();
        throw new TimeoutException(
          $"Rows not completed: {string.Join(",", open)}; failures: {failures.Describe()}");
      }
    }

    public ValueTask EnqueueEventWorkIdAsync(Guid eventWorkId, CancellationToken cancellationToken = default) =>
      _completeAsync([], [eventWorkId], cancellationToken);

    public ValueTask EnqueueCursorAsync(PerspectiveCursorCompletion cursor, CancellationToken cancellationToken = default) =>
      _completeAsync([cursor], [], cancellationToken);

    private async ValueTask _completeAsync(PerspectiveCursorCompletion[] cursors, Guid[] workIds, CancellationToken ct) {
      await using (var scope = scopeFactory.CreateAsyncScope()) {
        await scope.ServiceProvider.GetRequiredService<IWorkCoordinator>()
          .CompletePerspectiveAsync(cursors, workIds, debugMode: false, ct);
      }
      foreach (var workId in workIds) {
        _signal(workId).TrySetResult();
      }
    }

    private TaskCompletionSource _signal(Guid workId) =>
      _completed.GetOrAdd(workId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
  }

  /// <summary>Signals each failed row, with what failed it.</summary>
  private sealed class RecordingFailureChannel : IFailureChannel {
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<MessageFailure>> _failed = new();

    public string Describe() => string.Join("; ", _failed.Where(f => f.Value.Task.IsCompletedSuccessfully)
      .Select(f => $"{f.Key}: {f.Value.Task.Result.Error}"));

    public async Task<MessageFailure> WaitForAsync(Guid workId) {
      try {
        return await _signal(workId).Task.WaitAsync(_signalDeadline);
      } catch (TimeoutException) {
        throw new TimeoutException($"Row {workId} did not fail; failures: {Describe()}");
      }
    }

    public ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      _signal(failure.MessageId).TrySetResult(failure);
      return ValueTask.CompletedTask;
    }

    private TaskCompletionSource<MessageFailure> _signal(Guid workId) =>
      _failed.GetOrAdd(workId, static _ => new TaskCompletionSource<MessageFailure>(TaskCreationOptions.RunContinuationsAsynchronously));
  }
}
