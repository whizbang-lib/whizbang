using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Health;
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

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A perspective row whose stored document holds a number where the model property is a string, driven
/// through the real claim loop, perspective worker and generated runner against Postgres (issue #985).
/// </summary>
/// <remarks>
/// <para>
/// When the stream is touched again the runner's load of the current document fails. Older releases
/// retried such a stream forever: nothing was recorded against the rows, the lease lapsed, the rows were
/// re-claimed, and the same error repeated every cycle. What must happen instead: the failure is
/// classified as a stored document no reader takes, announced once, counted, recorded against every
/// leased row so the database schedules the retry with backoff and dead-letters at the threshold, and
/// surfaced as Degraded health, while every other stream of the perspective keeps flowing. When the
/// stored value is corrected, the next retry reads the stream again.
/// </para>
/// <para>
/// Everything below the worker is real: <c>commit_handler_result</c> writes the events, the claim loop
/// polls <c>claim_work</c>, the drain fetch leases through <c>get_stream_events</c>, the runner reads and
/// writes <c>wh_per_order</c> through Entity Framework. The completion and failure channels write through
/// to the coordinator (the same calls their flush workers make) before they return, which is what makes
/// "the database has recorded it" an event the test can wait on instead of a time it has to guess.
/// </para>
/// <para>
/// The one thing the test does to the clock is end a backoff early: a scheduled retry is 60 seconds out,
/// and moving <c>scheduled_for</c> to the past with SQL is the retry becoming due, without waiting for it.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/PerspectiveWorker.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Perspectives/StoredFormUnreadable.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/StoredFormMigrationSql.cs</code-under-test>
/// <docs>operations/infrastructure/migrations</docs>
/// <docs>fundamentals/perspectives/stored-form-migrations#recovery</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class StoredFormScalarMismatchWorkerTests : EFCoreTestBase {
  private static readonly TimeSpan _signalDeadline = TimeSpan.FromSeconds(60);

  [Test]
  [Timeout(180_000)]
  public async Task NumberForAStringProperty_IsClassifiedParkedAndDegraded_OthersFlow_AndACorrectedRowRecoversAsync(
      CancellationToken cancellationToken) {
    await using var pipeline = _composePipeline(maxFailures: 10, claimBatchSize: 100, drainConsumers: 1);
    await using var conn = await _openAsync(cancellationToken);
    await pipeline.StartAsync(conn, cancellationToken);

    var poisoned = (Guid)TrackedGuid.New();
    var healthy = (Guid)TrackedGuid.New();
    await pipeline.CommitAndWaitAppliedAsync(conn, [(poisoned, 10m), (healthy, 20m)], cancellationToken);

    await _storeStatusAsync(conn, poisoned, "123", cancellationToken);
    var failingEvent = await pipeline.CommitAsync(conn, [(poisoned, 11m)], cancellationToken);
    var failingWork = await _workIdForEventAsync(conn, failingEvent[0], cancellationToken);

    await pipeline.Failures.WaitAsync(items => items.Any(f => f.MessageId == failingWork), cancellationToken);

    // (a) classified, logged once at Error with the path, counted by reason.
    var announcements = pipeline.Logs.Snapshot().Where(l => l.EventId == 65 && l.StreamId == poisoned).ToList();
    await Assert.That(announcements).Count().IsEqualTo(1)
      .Because("an unreadable stored document is announced once per perspective and stream");
    await Assert.That(announcements[0].Level).IsEqualTo(LogLevel.Error);
    await Assert.That(announcements[0].Path).IsNotNull().And.Contains("Status")
      .Because("the announcement names where in the document the value the reader refused sits");
    var failure = pipeline.Failures.Snapshot().Single(f => f.MessageId == failingWork);
    await Assert.That(failure.Reason).IsEqualTo(MessageFailureReason.SerializationError);
    pipeline.MeterListener.RecordObservableInstruments();
    await Assert.That(pipeline.ReadFailureReasons.Snapshot()).Contains(StoredFormUnreadable.REASON)
      .Because("whizbang.perspective.read_failures counts the failure under stored_form_unreadable");

    // (b) the database recorded it and scheduled the retry with backoff.
    var parked = await _readWorkRowAsync(conn, failingWork, cancellationToken);
    await Assert.That(parked.Failures).IsEqualTo(1).Because("the failure is recorded against the row");
    await Assert.That(parked.FailureReason).IsEqualTo((int)MessageFailureReason.SerializationError);
    await Assert.That(parked.RetryDueInSeconds).IsGreaterThan(20)
      .Because("the retry is scheduled with backoff rather than on the next cycle");
    await Assert.That(parked.Leased).IsFalse().Because("the lease is released for the scheduled retry");

    // (e) the health component reports Degraded.
    var health = await pipeline.Health.ReportAsync(cancellationToken);
    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(health.Detail).IsNotNull().And.Contains(poisoned.ToString());

    // (d) other streams of the perspective keep processing, and the claims that serve them do not
    // lease the parked row again. A claim that carries a stream first committed after the park
    // necessarily ran after it, so those are the claims that must not carry the parked stream.
    var fresh = (Guid)TrackedGuid.New();
    await pipeline.CommitAndWaitAppliedAsync(conn, [(healthy, 21m), (fresh, 30m)], cancellationToken);
    await pipeline.CommitAndWaitAppliedAsync(conn, [(healthy, 22m), (fresh, 31m)], cancellationToken);
    var laterClaims = pipeline.Claims.Snapshot().Where(c => c.Contains(fresh)).ToList();
    await Assert.That(laterClaims).IsNotEmpty();
    await Assert.That(laterClaims.Any(c => c.Contains(poisoned))).IsFalse()
      .Because("a parked row is not claimable until its retry is due");
    var stillParked = await _readWorkRowAsync(conn, failingWork, cancellationToken);
    await Assert.That(stillParked.Failures).IsEqualTo(1).Because("no cycle applied the parked row again");
    await Assert.That(stillParked.Leased).IsFalse();
    await Assert.That(await _readAmountAsync(conn, healthy, cancellationToken)).IsEqualTo(22m);

    // (f) the stored value is corrected and the retry falls due: the stream reads and recovers.
    await _storeStatusAsync(conn, poisoned, "\"Corrected\"", cancellationToken);
    var recovered = pipeline.Applied.WaitAsync(items => items.Contains(poisoned), cancellationToken, fromNow: true);
    var completed = pipeline.Completed.WaitAsync(items => items.Contains(failingWork), cancellationToken);
    await _makeRetryDueAsync(conn, [failingWork], cancellationToken);
    await recovered;
    await completed;

    await Assert.That(await _readAmountAsync(conn, poisoned, cancellationToken)).IsEqualTo(11m)
      .Because("the event that could not be applied is applied once the document reads");
    await Assert.That(await _pendingRowExistsAsync(conn, failingWork, cancellationToken)).IsFalse()
      .Because("the recovered row is completed");
    var recoveredHealth = await pipeline.Health.ReportAsync(cancellationToken);
    await Assert.That(recoveredHealth.State).IsEqualTo(ComponentState.Operational)
      .Because("a stream that reads again is forgotten by the stored-forms health component");
    await Assert.That(pipeline.Logs.Snapshot().Count(l => l.EventId == 65 && l.StreamId == poisoned)).IsEqualTo(1);
  }

  /// <summary>
  /// The motivating case of issue #986, end to end: <c>Status</c> was an <c>int</c> and is now a <c>string</c>, the
  /// stored row still holds the number, the stream parks, and the stored-form migration the generator emits for
  /// <c>[StoredForm(Previously = typeof(int))]</c> converts it through the real rewrite phase. The stream then
  /// recovers on its next scheduled retry with no other step, and the migration is journaled.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task NumberForAStringProperty_RecoversOnItsNextRetry_AfterTheStoredFormMigrationConvertsItAsync(
      CancellationToken cancellationToken) {
    await using var pipeline = _composePipeline(maxFailures: 10, claimBatchSize: 100, drainConsumers: 1);
    await using var conn = await _openAsync(cancellationToken);
    await pipeline.StartAsync(conn, cancellationToken);

    var poisoned = (Guid)TrackedGuid.New();
    var healthy = (Guid)TrackedGuid.New();
    await pipeline.CommitAndWaitAppliedAsync(conn, [(poisoned, 10m), (healthy, 20m)], cancellationToken);

    // The row as a release whose model had `public int Status` wrote it.
    await _storeStatusAsync(conn, poisoned, "123", cancellationToken);
    var failingEvent = await pipeline.CommitAsync(conn, [(poisoned, 11m)], cancellationToken);
    var failingWork = await _workIdForEventAsync(conn, failingEvent[0], cancellationToken);
    await pipeline.Failures.WaitAsync(items => items.Any(f => f.MessageId == failingWork), cancellationToken);
    var parked = await _readWorkRowAsync(conn, failingWork, cancellationToken);
    await Assert.That(parked.Failures).IsEqualTo(1).Because("the stream is parked on the unreadable document");
    await Assert.That((await pipeline.Health.ReportAsync(cancellationToken)).State).IsEqualTo(ComponentState.Degraded);

    // The stored-form migration, exactly as the generator emits it for the declaration, through the real phase.
    var schema = (string)(await _scalarAsync(conn, "SELECT table_schema FROM information_schema.tables WHERE table_name = 'wh_per_order'", cancellationToken))!;
    var healthyBefore = await _scalarAsync(conn, $"SELECT data::text FROM wh_per_order WHERE id = '{healthy}'", cancellationToken);
    var migration = StoredFormMigrationSql.Generated(schema, "wh_per_order", "wh_per_order.Status:Int32->String", StoredFormStep.ToText("Status"));
    await StoredFormMigrationSql.DeclareAsync(() => new NpgsqlConnection(ConnectionString), schema, [migration], cancellationToken);
    var ran = await CanonicalTemporalRewritePhase.ApplyAsync(
      () => new NpgsqlConnection(ConnectionString), 986_986_986, StoredFormMigrationSql.ForPhase(schema, [migration]), 30,
      cancellationToken: cancellationToken);
    await Assert.That(ran).IsTrue();
    await Assert.That(await _scalarAsync(conn, $"SELECT (data -> 'Status')::text FROM wh_per_order WHERE id = '{poisoned}'", cancellationToken))
      .IsEqualTo("\"123\"").Because("the number the earlier release stored is now the string the model reads");
    await Assert.That(await _scalarAsync(conn, $"SELECT data::text FROM wh_per_order WHERE id = '{healthy}'", cancellationToken))
      .IsEqualTo(healthyBefore).Because("a row already in the new form is left alone");
    var status = await StoredFormMigrationJournal.ReadAsync(conn, schema, [migration], cancellationToken);
    await Assert.That(status.Single().State).IsEqualTo(StoredFormMigrationState.Applied);
    await Assert.That(status.Single().RowsConverted).IsEqualTo(1L);
    await Assert.That((await _readWorkRowAsync(conn, failingWork, cancellationToken)).Failures).IsEqualTo(1)
      .Because("the migration changes the document, not the retry schedule");

    // The retry falls due: the stream reads the converted document, applies the waiting event and recovers.
    var recovered = pipeline.Applied.WaitAsync(items => items.Contains(poisoned), cancellationToken, fromNow: true);
    var completed = pipeline.Completed.WaitAsync(items => items.Contains(failingWork), cancellationToken);
    await _makeRetryDueAsync(conn, [failingWork], cancellationToken);
    await recovered;
    await completed;

    await Assert.That(await _readAmountAsync(conn, poisoned, cancellationToken)).IsEqualTo(11m)
      .Because("the event that could not be applied is applied once the document reads");
    await Assert.That(await _pendingRowExistsAsync(conn, failingWork, cancellationToken)).IsFalse();
    await Assert.That((await pipeline.Health.ReportAsync(cancellationToken)).State).IsEqualTo(ComponentState.Operational)
      .Because("a stream that reads again is forgotten by the stored-forms health component");
  }

  [Test]
  [Timeout(180_000)]
  public async Task NumberForAStringProperty_PastTheThreshold_IsDeadLetteredAsync(CancellationToken cancellationToken) {
    await using var pipeline = _composePipeline(maxFailures: 1, claimBatchSize: 100, drainConsumers: 1);
    await using var conn = await _openAsync(cancellationToken);
    await pipeline.StartAsync(conn, cancellationToken);

    var poisoned = (Guid)TrackedGuid.New();
    await pipeline.CommitAndWaitAppliedAsync(conn, [(poisoned, 10m)], cancellationToken);
    await _storeStatusAsync(conn, poisoned, "123", cancellationToken);
    var failingEvent = await pipeline.CommitAsync(conn, [(poisoned, 11m)], cancellationToken);
    var work = await _workIdForEventAsync(conn, failingEvent[0], cancellationToken);

    // First failure, then the retry falls due and fails again: failures reaches 2, past a threshold of 1.
    await pipeline.Failures.WaitAsync(items => items.Count(f => f.MessageId == work) == 1, cancellationToken);
    await _makeRetryDueAsync(conn, [work], cancellationToken);
    await pipeline.Failures.WaitAsync(items => items.Count(f => f.MessageId == work) == 2, cancellationToken);
    await Assert.That((await _readWorkRowAsync(conn, work, cancellationToken)).Failures).IsEqualTo(2);

    await _makeRetryDueAsync(conn, [work], cancellationToken);
    await pipeline.DeadLetters.WaitAsync(items => items.Contains(work), cancellationToken);

    await Assert.That(await _pendingRowExistsAsync(conn, work, cancellationToken)).IsFalse()
      .Because("the dead-lettered row leaves the work table");
    await Assert.That(await _deadLetterExistsAsync(conn, work, cancellationToken)).IsTrue()
      .Because("the row is kept in wh_dead_letters for the operator");
    await Assert.That(pipeline.Failures.Snapshot().Count(f => f.MessageId == work)).IsEqualTo(2)
      .Because("the dead-lettered row is not applied, so it does not fail a third time");
  }

  /// <summary>
  /// Many poisoned streams of one per-stream perspective, well above the claim batch, must not starve
  /// the healthy ones, and once parked must not be re-claimed until their backoff is due.
  /// </summary>
  [Test]
  [Timeout(300_000)]
  public async Task ManyPoisonedStreams_DoNotStarveHealthyOnes_AndAreNotReclaimedWhileParkedAsync(
      CancellationToken cancellationToken) {
    const int claimBatchSize = 20;
    const int poisonedCount = 120;
    await using var pipeline = _composePipeline(maxFailures: 10, claimBatchSize: claimBatchSize, drainConsumers: 4);
    await using var conn = await _openAsync(cancellationToken);
    await pipeline.StartAsync(conn, cancellationToken);

    var poisoned = Enumerable.Range(0, poisonedCount).Select(_ => (Guid)TrackedGuid.New()).ToList();
    var healthy = Enumerable.Range(0, 3).Select(_ => (Guid)TrackedGuid.New()).ToList();
    await pipeline.CommitAndWaitAppliedAsync(conn, [.. poisoned.Concat(healthy).Select(s => (s, 1m))], cancellationToken);
    foreach (var stream in poisoned) {
      await _storeStatusAsync(conn, stream, "123", cancellationToken);
    }

    // The poisoned streams' events are committed first, so they are ahead of the healthy ones in
    // every claim; the healthy events follow in a later commit.
    var poisonedEvents = await pipeline.CommitAsync(conn, [.. poisoned.Select(s => (s, 2m))], cancellationToken);
    var healthyApplied = pipeline.Applied.WaitAsync(items => healthy.All(items.Contains), cancellationToken, fromNow: true);
    await pipeline.CommitAsync(conn, [.. healthy.Select(s => (s, 3m))], cancellationToken);
    await healthyApplied;

    var poisonedWork = new List<Guid>();
    foreach (var eventId in poisonedEvents) {
      poisonedWork.Add(await _workIdForEventAsync(conn, eventId, cancellationToken));
    }
    await pipeline.Failures.WaitAsync(items => poisonedWork.All(w => items.Any(f => f.MessageId == w)), cancellationToken);

    // The healthy streams were applied within the first pass over the poisoned backlog ahead of them.
    // Their wait is bounded by the first retry's 60-second backoff, and every poisoned row
    // holds exactly one failure: each was parked on first contact, and none had to be tried again
    // before the healthy streams got through.
    foreach (var work in poisonedWork) {
      await Assert.That((await _readWorkRowAsync(conn, work, cancellationToken)).Failures).IsEqualTo(1)
        .Because("each poisoned row failed on its first pass and was parked, once (issue #987)");
    }
    foreach (var stream in healthy) {
      await Assert.That(await _readAmountAsync(conn, stream, cancellationToken)).IsEqualTo(3m);
    }

    // Once parked, no poisoned stream is claimed again: more healthy traffic runs through several
    // claims, none of them carries a poisoned stream, and no poisoned row fails again. Each round adds
    // a stream first committed after the park, which marks the claims that ran after it.
    var fresh = new List<Guid>();
    for (var round = 0; round < 3; round++) {
      var stream = (Guid)TrackedGuid.New();
      fresh.Add(stream);
      await pipeline.CommitAndWaitAppliedAsync(conn, [.. healthy.Append(stream).Select(s => (s, 4m + round))], cancellationToken);
    }
    var laterClaims = pipeline.Claims.Snapshot().Where(c => c.Overlaps(fresh)).ToList();
    await Assert.That(laterClaims).IsNotEmpty();
    await Assert.That(laterClaims.Where(c => c.Overlaps(poisoned)).ToList()).IsEmpty()
      .Because("a parked stream is not claimed before its retry is due");
    foreach (var work in poisonedWork) {
      var row = await _readWorkRowAsync(conn, work, cancellationToken);
      await Assert.That(row.Failures).IsEqualTo(1).Because("the drain is not churning on parked rows");
      await Assert.That(row.RetryDueInSeconds).IsGreaterThan(20);
      await Assert.That(row.Leased).IsFalse();
    }
    await Assert.That(pipeline.Logs.Snapshot().Count(l => l.EventId == 65)).IsEqualTo(poisonedCount)
      .Because("each poisoned stream is announced exactly once");
    var health = await pipeline.Health.ReportAsync(cancellationToken);
    await Assert.That(health.State).IsEqualTo(ComponentState.Degraded);
    await Assert.That(health.Detail).IsNotNull().And.Contains($"{poisonedCount} stream(s)");
  }

  // ============================================================================
  // pipeline
  // ============================================================================

  /// <summary>Composes the pipeline.</summary>
  /// <param name="maxFailures">The dead-letter threshold.</param>
  /// <param name="claimBatchSize">The claim's stream window, fixed.</param>
  /// <param name="drainConsumers">
  /// Drain consumer loops. The tests that count reports per row use one: with several, a stream the claim
  /// re-offered while its first queue entry waited can be drained twice concurrently, and both drains
  /// report the one lease's failure (issue #987). The database counts that once, but the number of
  /// reports is then a race. The many-streams test runs the default four and asserts on the database.
  /// </param>
  private Pipeline _composePipeline(int maxFailures, int claimBatchSize, int drainConsumers) {
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

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
      services, typeof(WorkCoordinationDbContext), typeof(Order), "wh_per_order",
      new PostgresUpsertStrategy());
    services.AddMetrics();
    var provider = services.BuildServiceProvider();
    var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
    var instanceProvider = new ServiceInstanceProvider(config);
    var harness = new PerspectiveWorkerTestHarness();

    var claims = new Recorder<HashSet<Guid>>();
    var failures = new Recorder<MessageFailure>();
    var applied = new Recorder<Guid>();
    var completed = new Recorder<Guid>();
    var deadLetters = new Recorder<Guid>();
    var logs = new Recorder<LogRecord>();
    var readFailureReasons = new Recorder<string>();

    var claimWorker = new ClaimWorker(
      scopeFactory: scopeFactory,
      instanceProvider: instanceProvider,
      notificationListener: new NoOpWorkNotificationListener(),
      schemaReadyGate: gate,
      options: Options.Create(new ClaimWorkerOptions {
        PollingIntervalMilliseconds = 50,
        PollingMaxIntervalMilliseconds = 200,
        AdaptiveClaimWindow = false,
        AdaptiveOutstandingBudget = false,
        MaxStreamsPerBatch = claimBatchSize,
      }),
      logger: NullLogger<ClaimWorker>.Instance,
      outboxChannel: new WorkChannelWriter(),
      inboxChannel: new InboxChannelWriter(),
      perspectiveChannel: harness.ChannelWriter,
      perspectiveDrainChannel: harness.DrainChannel,
      outboxDrainChannel: new OutboxDrainChannel(),
      inboxDrainChannel: new InboxDrainChannel(),
      signalingGate: NullNotifySignalingGate.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance,
      signalBus: NullSignalBus.Instance);
    claimWorker.OnBatchClaimed += batch =>
      claims.Add([.. batch.PerspectiveStreamIds, .. batch.PerspectiveWork.Select(w => w.StreamId)]);

    var registry = new StoredFormFailureRegistry();
    var metrics = new PerspectiveMetrics(new WhizbangMetrics(provider.GetRequiredService<IMeterFactory>()));
    var meterListener = new MeterListener {
      InstrumentPublished = (instrument, listener) => {
        if (ReferenceEquals(instrument, metrics.ReadFailures.Instrument)) {
          listener.EnableMeasurementEvents(instrument);
        }
      }
    };
    meterListener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      foreach (var tag in tags) {
        if (tag.Key == "reason" && tag.Value is string reason && value > 0) {
          readFailureReasons.Add(reason);
        }
      }
    });
    meterListener.Start();

    var workerOptions = new PerspectiveWorkerOptions {
      MaxPerspectiveEventAttempts = maxFailures,
      MaxConcurrentDrainConsumers = drainConsumers,
    };
    var perspectiveWorker = new PerspectiveWorker(
      instanceProvider: instanceProvider,
      scopeFactory: scopeFactory,
      options: Options.Create(workerOptions),
      schemaReadyGate: gate,
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      completionStrategy: new InstantCompletionStrategy(NullLogger<InstantCompletionStrategy>.Instance),
      eventTypeProvider: provider.GetRequiredService<IEventTypeProvider>(),
      syncSignaler: new LocalSyncSignaler(NullLogger<LocalSyncSignaler>.Instance),
      syncEventTracker: new SyncEventTracker(),
      logger: new RecordingLogger<PerspectiveWorker>(logs),
      snapshotStore: NullPerspectiveSnapshotStore.Instance,
      streamLocker: NullPerspectiveStreamLocker.Instance,
      streamLockOptions: Options.Create(new PerspectiveStreamLockOptions()),
      streamAffinityOptions: Options.Create(new PerspectiveStreamAffinityOptions()),
      processedEventCacheObserver: NullProcessedEventCacheObserver.Instance,
      workChannelWriter: new WorkChannelWriter(),
      rewindOptions: Options.Create(new PerspectiveRewindOptions()),
      perspectiveChannelWriter: harness.ChannelWriter,
      perspectiveCompletionChannel: new WriteThroughCompletionChannel(scopeFactory, completed),
      failureChannel: new WriteThroughFailureChannel(scopeFactory, failures),
      leaseRenewalChannel: new CapturingLeaseRenewalChannel(),
      perspectiveDrainChannel: harness.DrainChannel,
      leaseHandleOptions: Options.Create(new LeaseHandleOptions()),
      leaseRenewalOptions: Options.Create(new LeaseRenewalWorkerOptions()),
      deadLetterStore: new RecordingDeadLetterStore(scopeFactory, deadLetters),
      generationProvider: new DefaultGenerationProvider(),
      perspectiveNotificationListener: new NoOpWorkNotificationListener(),
      governor: PerspectiveWorker.CreateDefaultGovernor(workerOptions),
      metrics: metrics,
      storedFormFailures: registry);
    perspectiveWorker.OnPerspectiveEventProcessed += e => applied.Add(e.StreamId);

    return new Pipeline {
      Services = provider,
      InstanceProvider = instanceProvider,
      ClaimWorker = claimWorker,
      PerspectiveWorker = perspectiveWorker,
      MeterListener = meterListener,
      Health = new StoredFormHealthSource(registry),
      Claims = claims,
      Failures = failures,
      Applied = applied,
      Completed = completed,
      DeadLetters = deadLetters,
      Logs = logs,
      ReadFailureReasons = readFailureReasons,
    };
  }

  private sealed class Pipeline : IAsyncDisposable {
    public required ServiceProvider Services { get; init; }
    public required ServiceInstanceProvider InstanceProvider { get; init; }
    public required ClaimWorker ClaimWorker { get; init; }
    public required PerspectiveWorker PerspectiveWorker { get; init; }
    public required MeterListener MeterListener { get; init; }
    public required StoredFormHealthSource Health { get; init; }
    public required Recorder<HashSet<Guid>> Claims { get; init; }
    public required Recorder<MessageFailure> Failures { get; init; }
    public required Recorder<Guid> Applied { get; init; }
    public required Recorder<Guid> Completed { get; init; }
    public required Recorder<Guid> DeadLetters { get; init; }
    public required Recorder<LogRecord> Logs { get; init; }
    public required Recorder<string> ReadFailureReasons { get; init; }
    private bool _started;

    public async Task StartAsync(NpgsqlConnection conn, CancellationToken ct) {
      await _registerInstanceAsync(conn, InstanceProvider.InstanceId, ct);
      await _seedPerspectiveAssociationAsync(conn, ct);
      _started = true;
      await ClaimWorker.StartAsync(ct);
      await PerspectiveWorker.StartAsync(ct);
      await PerspectiveWorker.StartupScanComplete.WaitAsync(_signalDeadline, ct);
    }

    /// <summary>Commits one event per entry, each on its stream, and returns the event ids in order.</summary>
    public async Task<List<Guid>> CommitAsync(NpgsqlConnection conn, IReadOnlyList<(Guid StreamId, decimal Amount)> events, CancellationToken ct) {
      var ids = events.Select(_ => (Guid)TrackedGuid.New()).ToList();
      await using (var call = conn.CreateCommand()) {
        call.CommandText = "SELECT commit_handler_result(@req::jsonb)";
        call.Parameters.AddWithValue("req", _buildCommitRequest(InstanceProvider, events, ids));
        _ = await call.ExecuteScalarAsync(ct);
      }
      // The commit-order stamper's job, done in line: the claim gates unstamped rows behind a grace
      // window, and stamping here is what the stamper would do on the commit's wake.
      await using (var stamp = conn.CreateCommand()) {
        stamp.CommandText = "SELECT stamp_pending_commit_sequences(10000, false)";
        _ = await stamp.ExecuteScalarAsync(ct);
      }
      return ids;
    }

    /// <summary>Commits and waits until each entry's stream has been applied since the commit.</summary>
    public async Task CommitAndWaitAppliedAsync(NpgsqlConnection conn, IReadOnlyList<(Guid StreamId, decimal Amount)> events, CancellationToken ct) {
      var streams = events.Select(e => e.StreamId).Distinct().ToList();
      var applied = Applied.WaitAsync(items => streams.All(items.Contains), ct, fromNow: true);
      await CommitAsync(conn, events, ct);
      await applied;
    }

    private static async Task _registerInstanceAsync(NpgsqlConnection conn, Guid instanceId, CancellationToken ct) {
      await using var reg = conn.CreateCommand();
      reg.CommandText = """
        INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at, metadata)
        VALUES (@id, 'stored-form-svc', 'stored-form-host', 1, NOW(), NOW(), '{}'::jsonb)
        ON CONFLICT (instance_id) DO UPDATE SET last_heartbeat_at = NOW()
        """;
      reg.Parameters.AddWithValue("id", instanceId);
      await reg.ExecuteNonQueryAsync(ct);
    }

    private static async Task _seedPerspectiveAssociationAsync(NpgsqlConnection conn, CancellationToken ct) {
      var eventType = TypeNameFormatter.Format(typeof(SampleOrderCreatedEvent));
      var perspectiveName = TypeNameFormatter.GetPerspectiveName(typeof(OrderPerspective));
      await using var assoc = conn.CreateCommand();
      assoc.CommandText = """
        INSERT INTO wh_message_associations
          (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
        VALUES (gen_random_uuid(), @eventType, 'perspective', @target, 'stored-form-svc', @eventType, NOW(), NOW())
        ON CONFLICT DO NOTHING
        """;
      assoc.Parameters.AddWithValue("eventType", eventType);
      assoc.Parameters.AddWithValue("target", perspectiveName);
      await assoc.ExecuteNonQueryAsync(ct);
    }

    private static string _buildCommitRequest(
        IServiceInstanceProvider instance, IReadOnlyList<(Guid StreamId, decimal Amount)> events, IReadOnlyList<Guid> eventIds) {
      var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
      var eventType = TypeNameFormatter.Format(typeof(SampleOrderCreatedEvent));
      var messages = new List<string>(events.Count);
      for (var i = 0; i < events.Count; i++) {
        var (streamId, amount) = events[i];
        var payload = new SampleOrderCreatedEvent { OrderId = new TestOrderId(streamId), Amount = amount };
        var payloadJson = JsonSerializer.Serialize(payload, jsonOptions.GetTypeInfo(typeof(SampleOrderCreatedEvent)));
        var envelope = new MessageEnvelope<JsonElement> {
          MessageId = MessageId.From(eventIds[i]),
          DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
          Hops = [],
          Payload = JsonDocument.Parse(payloadJson).RootElement
        };
        var envelopeJson = JsonSerializer.Serialize(envelope, jsonOptions.GetTypeInfo(typeof(MessageEnvelope<JsonElement>)));
        messages.Add($$"""
          {
            "MessageId": "{{eventIds[i]}}",
            "Destination": "order-events",
            "MessageType": "{{eventType}}",
            "EnvelopeType": null,
            "Envelope": {{envelopeJson}},
            "Metadata": {},
            "Scope": null,
            "StreamId": "{{streamId}}",
            "IsEvent": true
          }
          """);
      }

      return $$"""
        {
          "instance_id": "{{instance.InstanceId}}",
          "service_name": "{{instance.ServiceName}}",
          "host_name": "{{instance.HostName}}",
          "process_id": {{instance.ProcessId}},
          "new_outbox_messages": [{{string.Join(",", messages)}}]
        }
        """;
    }

    public async ValueTask DisposeAsync() {
      if (_started) {
        await PerspectiveWorker.StopAsync(CancellationToken.None);
        await ClaimWorker.StopAsync(CancellationToken.None);
      }
      MeterListener.Dispose();
      await Services.DisposeAsync();
    }
  }

  /// <summary>Reports each failure to the coordinator before returning, then records it.</summary>
  private sealed class WriteThroughFailureChannel(IServiceScopeFactory scopeFactory, Recorder<MessageFailure> recorded) : IFailureChannel {
    public async ValueTask EnqueueAsync(WorkCategory category, MessageFailure failure, CancellationToken cancellationToken = default) {
      await using (var scope = scopeFactory.CreateAsyncScope()) {
        await scope.ServiceProvider.GetRequiredService<IWorkCoordinator>()
          .ReportFailuresAsync(category, [failure], cancellationToken);
      }
      recorded.Add(failure);
    }
  }

  /// <summary>Completes each row and cursor at the coordinator before returning, then records the rows.</summary>
  /// <remarks>
  /// The worker announces an applied stream before its completions are flushed, so "the row is
  /// completed" is this record, not the announcement.
  /// </remarks>
  private sealed class WriteThroughCompletionChannel(IServiceScopeFactory scopeFactory, Recorder<Guid> completed) : IPerspectiveCompletionChannel {
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
        completed.Add(workId);
      }
    }
  }

  /// <summary>The Entity Framework dead-letter store, one context per move, recording what it moved.</summary>
  private sealed class RecordingDeadLetterStore(IServiceScopeFactory scopeFactory, Recorder<Guid> moved) : IDeadLetterStore {
    public async Task<Guid?> MoveAsync(Guid deadLetterId, string sourceTable, Guid sourceId, MessageFailureReason failureReason,
        string? errorText, Guid instanceId, string generation, CancellationToken ct = default) {
      await using var scope = scopeFactory.CreateAsyncScope();
      var store = new EFCoreDeadLetterStore<WorkCoordinationDbContext>(
        scope.ServiceProvider.GetRequiredService<WorkCoordinationDbContext>());
      var result = await store.MoveAsync(deadLetterId, sourceTable, sourceId, failureReason, errorText, instanceId, generation, ct);
      moved.Add(sourceId);
      return result;
    }
  }

  private sealed record LogRecord(LogLevel Level, int EventId, Guid? StreamId, string? Path, string Message);

  private sealed class RecordingLogger<T>(Recorder<LogRecord> records) : ILogger<T> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Guid? streamId = null;
      string? path = null;
      if (state is IReadOnlyList<KeyValuePair<string, object?>> values) {
        foreach (var (key, value) in values) {
          if (key == "StreamId" && value is Guid id) {
            streamId = id;
          } else if (key == "Path") {
            path = value?.ToString();
          }
        }
      }
      records.Add(new LogRecord(logLevel, eventId.Id, streamId, path, formatter(state, exception)));
    }
  }

  /// <summary>
  /// An append-only record that a test can wait on: a wait completes the moment the recorded items
  /// satisfy its condition, so nothing is polled and nothing sleeps.
  /// </summary>
  private sealed class Recorder<T> {
    private readonly Lock _lock = new();
    private readonly List<T> _items = [];
    private readonly List<(int From, Func<IReadOnlyList<T>, bool> Done, TaskCompletionSource Signal)> _waiters = [];

    public void Add(T item) {
      lock (_lock) {
        _items.Add(item);
        for (var i = _waiters.Count - 1; i >= 0; i--) {
          var (from, done, signal) = _waiters[i];
          if (done(_items.GetRange(from, _items.Count - from))) {
            signal.TrySetResult();
            _waiters.RemoveAt(i);
          }
        }
      }
    }

    public List<T> Snapshot() {
      lock (_lock) {
        return [.. _items];
      }
    }

    /// <summary>
    /// Completes when the items recorded (from now on, when <paramref name="fromNow"/>) satisfy
    /// <paramref name="done"/>; fails the test if that has not happened by the deadline.
    /// </summary>
    public Task WaitAsync(Func<IReadOnlyList<T>, bool> done, CancellationToken ct, bool fromNow = false) {
      var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      lock (_lock) {
        var from = fromNow ? _items.Count : 0;
        if (done(_items.GetRange(from, _items.Count - from))) {
          return Task.CompletedTask;
        }
        _waiters.Add((from, done, signal));
      }
      return signal.Task.WaitAsync(_signalDeadline, ct);
    }
  }

  // ============================================================================
  // SQL helpers
  // ============================================================================

  private sealed record WorkRow(int Failures, int FailureReason, double RetryDueInSeconds, bool Leased);

  private async Task<NpgsqlConnection> _openAsync(CancellationToken ct) {
    var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(ct);
    return conn;
  }

  private static async Task _storeStatusAsync(NpgsqlConnection conn, Guid streamId, string jsonValue, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_per_order SET data = jsonb_set(data, '{Status}', @value::jsonb) WHERE id = @id";
    cmd.Parameters.AddWithValue("value", jsonValue);
    cmd.Parameters.AddWithValue("id", streamId);
    await Assert.That(await cmd.ExecuteNonQueryAsync(ct)).IsEqualTo(1).Because("the applied row must exist to be changed");
  }

  private static async Task<object?> _scalarAsync(NpgsqlConnection conn, string sql, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var value = await cmd.ExecuteScalarAsync(ct);
    return value is DBNull ? null : value;
  }

  private static async Task<Guid> _workIdForEventAsync(NpgsqlConnection conn, Guid eventId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT event_work_id FROM wh_perspective_events WHERE event_id = @eid";
    cmd.Parameters.AddWithValue("eid", eventId);
    return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task<WorkRow> _readWorkRowAsync(NpgsqlConnection conn, Guid workId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT failures, COALESCE(failure_reason, 0)::int,
             COALESCE(EXTRACT(EPOCH FROM (scheduled_for - NOW())), 0)::float8,
             (instance_id IS NOT NULL AND lease_expiry > NOW())
      FROM wh_perspective_events WHERE event_work_id = @work
      """;
    cmd.Parameters.AddWithValue("work", workId);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    await Assert.That(await reader.ReadAsync(ct)).IsTrue().Because("the parked row stays in the work table");
    return new WorkRow(reader.GetInt32(0), reader.GetInt32(1), reader.GetDouble(2), reader.GetBoolean(3));
  }

  private static async Task _makeRetryDueAsync(NpgsqlConnection conn, IReadOnlyList<Guid> workIds, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE wh_perspective_events SET scheduled_for = NOW() - INTERVAL '1 second' WHERE event_work_id = ANY(@work)";
    cmd.Parameters.AddWithValue("work", workIds.ToArray());
    await cmd.ExecuteNonQueryAsync(ct);
  }

  private static async Task<bool> _pendingRowExistsAsync(NpgsqlConnection conn, Guid workId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM wh_perspective_events WHERE event_work_id = @work AND processed_at IS NULL)";
    cmd.Parameters.AddWithValue("work", workId);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task<bool> _deadLetterExistsAsync(NpgsqlConnection conn, Guid workId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM wh_dead_letters WHERE source_id = @work)";
    cmd.Parameters.AddWithValue("work", workId);
    return (bool)(await cmd.ExecuteScalarAsync(ct))!;
  }

  private static async Task<decimal> _readAmountAsync(NpgsqlConnection conn, Guid streamId, CancellationToken ct) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT (data ->> 'Amount')::numeric FROM wh_per_order WHERE id = @id";
    cmd.Parameters.AddWithValue("id", streamId);
    return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
  }
}
