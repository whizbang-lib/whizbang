// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Every statement the Dapper work coordinator runs reaches the schema it is configured for, on a session whose
/// <c>search_path</c> does not lead there (#1508).
/// </summary>
/// <remarks>
/// <para>
/// The coordinator wrote bare names, resolved through the session's <c>search_path</c>, and a few statements named
/// <c>public</c> outright. A pooled connection (PgBouncer in transaction mode, for one) does not carry the
/// <c>search_path</c> a host configured to the server connection the statement lands on, so on such a pool the
/// coordinator read and wrote <c>public</c>, or failed there, while the framework's tables lived in the host's schema.
/// </para>
/// <para>
/// The database here is migrated into <see cref="SCHEMA"/> and leaves <c>public</c> empty; the coordinator's own
/// connection string has the server's default path. A statement that is not qualified with the configured schema finds
/// nothing in <c>public</c> and fails, and a doorbell ring that misses is logged as a warning, which is asserted absent.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs</code-under-test>
[Category("Integration")]
public class DapperWorkCoordinatorNamedSchemaTests {
  // Later than any database clock reads, so a hold or deferral is still in force when the sweep runs.
  private static readonly DateTimeOffset _heldUntil = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private const string SCHEMA = "svc_coord";
  private const string SERVICE = "named-schema-svc";
  private const string HOST = "named-schema-host";
  private const string PERSPECTIVE = "NamedSchemaPerspective";
  private const string MESSAGE_TYPE = "Test.NamedSchema, Test";
  private const int PARTITIONS = 100;

  private PerTestDatabase _database;
  private readonly WarningLog _log = new();

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("dapper_named");
    await using (var connection = new NpgsqlConnection(_database.ConnectionString)) {
      await connection.OpenAsync();
      await connection.ExecuteAsync($"CREATE SCHEMA {SCHEMA}");
    }
    // The infrastructure tables land in the schema the migrating connection resolves first, and the migrations in the
    // schema their provider renders, so both are pointed at the named schema. The coordinator below gets neither.
    var migrating = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { SearchPath = SCHEMA }.ConnectionString;
    var migrations = new Whizbang.Data.Postgres.PostgresMigrationProvider(typeof(Whizbang.Data.Postgres.PostgresMigrationProvider).Assembly, SCHEMA);
    await new PostgresSchemaInitializer(migrating, [], migrations).InitializeSchemaAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  private DapperWorkCoordinator _build() =>
    new(_database.ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), _log, schema: SCHEMA);

  [Test]
  public async Task ThePremise_TheSchemaHoldsTheTables_AndTheCoordinatorsSessionResolvesPublicAsync() {
    await using var connection = await _openAsync();

    await Assert.That(await connection.ExecuteScalarAsync<string>("SELECT current_schema()")).IsEqualTo("public");
    await Assert.That(await connection.ExecuteScalarAsync<string?>("SELECT to_regclass('public.wh_outbox')::text")).IsNull()
      .Because("a statement that missed the configured schema must fail rather than find a second copy in public");
    await Assert.That(await connection.ExecuteScalarAsync<string?>($"SELECT to_regclass('{SCHEMA}.wh_outbox')::text")).IsNotNull();
  }

  [Test]
  public async Task TheInstanceLifecycle_RunsInTheConfiguredSchemaAsync() {
    var coordinator = _build();
    var instanceId = Guid.CreateVersion7();

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, SERVICE, HOST, 7));
    await Assert.That(await _countAsync("wh_service_instances", "instance_id = @id", new { id = instanceId })).IsEqualTo(1L);

    var batch = await coordinator.ClaimWorkAsync(new ClaimWorkRequest(
      instanceId, SERVICE, HOST, 7, MaxStreams: 10, PartitionCount: PARTITIONS, LeaseSeconds: 300));
    await Assert.That(batch.PerspectiveStreamIds).IsEmpty();
    var outstanding = await coordinator.CountOutstandingWorkAsync(instanceId);
    await Assert.That(outstanding!.InboxRows + outstanding.OutboxRows + outstanding.PerspectiveRows).IsEqualTo(0L);
    await Assert.That(await coordinator.NotifyScheduledRetryDueAsync()).IsEqualTo(0);
    await Assert.That((await coordinator.RecomputePartitionNumbersAsync(PARTITIONS)).OutboxRowsRecomputed).IsEqualTo(0L);
    await Assert.That((await coordinator.GatherStatisticsAsync()).PendingOutbox).IsEqualTo(0);

    await coordinator.DeregisterInstanceAsync(instanceId);
    await Assert.That(await _countAsync("wh_service_instances", "instance_id = @id", new { id = instanceId })).IsEqualTo(0L);
    await _assertNoMissedRingsAsync();
  }

  [Test]
  public async Task TheOutboxPath_RunsInTheConfiguredSchemaAsync() {
    var coordinator = _build();
    var instanceId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var leased = Guid.CreateVersion7();
    var pending = Guid.CreateVersion7();

    await coordinator.StoreOutboxMessagesAsync([_outbox(leased, streamId), _outbox(pending, Guid.CreateVersion7())], PARTITIONS);
    await Assert.That(await _countAsync("wh_outbox", "message_id = ANY(@ids)", new { ids = new[] { leased, pending } })).IsEqualTo(2L);
    await Assert.That(await coordinator.GetStreamsWithPendingMessagesAsync([streamId], [MESSAGE_TYPE])).Contains(streamId);

    await _executeAsync(
      $"UPDATE {SCHEMA}.wh_outbox SET instance_id = @i, lease_expiry = NOW() + INTERVAL '5 minutes' WHERE message_id = @m",
      new { i = instanceId, m = leased });
    await Assert.That(await coordinator.FetchOutboxBatchAsync([streamId], instanceId)).Count().IsEqualTo(1);
    await Assert.That(await coordinator.FetchOutboxBatchAsync([streamId], instanceId, 10, maxBytes: 1_000_000)).Count().IsEqualTo(1);
    await Assert.That(await coordinator.ContinueOutboxStreamsAsync([new OutboxStreamCursor(streamId, leased)], instanceId, 3, null)).IsEmpty();
    await Assert.That(await coordinator.RenewLeasesAsync(WorkCategory.Outbox, [leased], 120)).IsEqualTo(1);
    await coordinator.ReportFailuresAsync(WorkCategory.Outbox,
      [new MessageFailure { MessageId = leased, CompletedStatus = MessageProcessingStatus.Stored, Error = "named-schema failure" }]);
    await Assert.That(await _scalarAsync<string?>($"SELECT error FROM {SCHEMA}.wh_outbox WHERE message_id = @m", new { m = leased }))
      .IsEqualTo("named-schema failure");
    await Assert.That(await coordinator.CompleteOutboxPublishedAsync([leased], debugMode: false)).IsEqualTo(1);
    await Assert.That(await coordinator.CleanupCompletedStreamsAsync([streamId])).IsGreaterThanOrEqualTo(0);

    await Assert.That(await coordinator.DiscardPendingOutboxMessagesAsync([MESSAGE_TYPE])).IsEqualTo(1L);
    await Assert.That(await _countAsync("wh_outbox", "message_id = @m", new { m = pending })).IsEqualTo(0L);
    await _assertNoMissedRingsAsync();
  }

  [Test]
  public async Task TheInboxPath_RunsInTheConfiguredSchemaAsync() {
    var coordinator = _build();
    var instanceId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var first = Guid.CreateVersion7();
    var second = Guid.CreateVersion7();
    var discarded = Guid.CreateVersion7();

    await coordinator.StoreInboxMessagesAsync([_inbox(first, streamId)], PARTITIONS);
    var observations = await coordinator.StoreInboxMessagesWithObservationsAsync([_inbox(first, streamId), _inbox(second, streamId)], PARTITIONS);
    await Assert.That(observations.Select(o => o.MessageId)).Contains(first)
      .Because("the redelivered message is observed from the configured schema's inbox");
    await Assert.That(await _countAsync("wh_inbox", "message_id = ANY(@ids)", new { ids = new[] { first, second } })).IsEqualTo(2L);

    await _executeAsync(
      // Leased and stored, as a claim leaves them: fetch_inbox_batch hands an event out once it is stored (#1607).
      $"UPDATE {SCHEMA}.wh_inbox_state SET instance_id = @i, lease_expiry = NOW() + INTERVAL '5 minutes', chain_emitted_at = NOW() WHERE message_id = ANY(@m)",
      new { i = instanceId, m = new[] { first, second } });
    await Assert.That(await coordinator.FetchInboxBatchAsync([streamId], instanceId)).Count().IsEqualTo(2);
    await Assert.That(await coordinator.FetchInboxBatchAsync([streamId], instanceId, 10, maxBytes: 1_000_000)).Count().IsEqualTo(2);

    await coordinator.CommitHandlerResultAsync(_commit(instanceId, first));
    var results = await coordinator.CommitHandlerBatchAsync([_commit(instanceId, second)]);
    await Assert.That(results.Single().Success).IsTrue();

    await coordinator.StoreInboxMessagesAsync([_inbox(discarded, Guid.CreateVersion7())], PARTITIONS);
    await Assert.That(await coordinator.DiscardPendingInboxMessagesAsync([MESSAGE_TYPE])).IsEqualTo(1L);
    await Assert.That(await coordinator.PurgeOrphanInboxAsync([MESSAGE_TYPE])).IsEmpty();
    await _assertNoMissedRingsAsync();
  }

  [Test]
  public async Task ThePerspectivePath_RunsInTheConfiguredSchemaAsync() {
    var coordinator = _build();
    var instanceId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    await _executeAsync(
      $"INSERT INTO {SCHEMA}.wh_perspective_cursors (stream_id, perspective_name, last_event_id, status) VALUES (@s, @p, @e, 0)",
      new { s = streamId, p = PERSPECTIVE, e = eventId });

    var cursor = await coordinator.GetPerspectiveCursorAsync(streamId, PERSPECTIVE);
    await Assert.That(cursor!.LastEventId).IsEqualTo(eventId);
    await coordinator.ReportPerspectiveFailureAsync(new PerspectiveCursorFailure {
      StreamId = streamId,
      PerspectiveName = PERSPECTIVE,
      LastEventId = eventId,
      ProcessedEventIds = [],
      Status = PerspectiveProcessingStatus.Failed,
      Error = "named-schema boom",
    });
    await Assert.That(await _scalarAsync<string?>(
      $"SELECT error FROM {SCHEMA}.wh_perspective_cursors WHERE stream_id = @s", new { s = streamId })).IsEqualTo("named-schema boom");
    await coordinator.ReportPerspectiveCompletionAsync(_completion(streamId, eventId));
    await coordinator.CompletePerspectiveAsync([_completion(streamId, eventId)], [], debugMode: false);
    await coordinator.FlushCompletionsAsync(new FlushCompletionsRequest(PerspectiveCursors: [_completion(streamId, eventId)]));

    await Assert.That(await coordinator.CompletePerspectiveEventsAsync([Guid.CreateVersion7()], debugMode: false)).IsEqualTo(0);
    await Assert.That(await coordinator.GetStreamEventsAsync(instanceId, [streamId])).IsEmpty();
    await Assert.That(await coordinator.FetchPendingPerspectiveEventsAsync(streamId, PERSPECTIVE, instanceId)).IsEmpty();
    await Assert.That(await coordinator.ClaimAndFetchPendingPerspectiveEventsAsync(streamId, PERSPECTIVE, instanceId, TimeSpan.FromMinutes(1))).IsEmpty();
    await Assert.That(await coordinator.FetchCollectiveSinkQueueAsync(streamId)).IsEmpty();
    await Assert.That(await coordinator.FetchEventsByIdsAsync([eventId])).IsEmpty();
    var sync = await coordinator.ResolveSyncInquiriesAsync([new SyncInquiry { StreamId = streamId, PerspectiveName = PERSPECTIVE }]);
    await Assert.That(sync.Single().PendingCount).IsEqualTo(0);
    var applied = await coordinator.GetAppliedEventStatusAsync(new AppliedEventInquiry(PERSPECTIVE, eventId));
    await Assert.That(applied!.Value.State).IsEqualTo(AppliedEventState.NotArrived);
    await _assertNoMissedRingsAsync();
  }

  [Test]
  public async Task Maintenance_RunsInTheConfiguredSchemaAsync() {
    var coordinator = _build();

    await coordinator.SyncDebugRetentionSettingAsync(debugMode: true);
    await Assert.That(await _scalarAsync<string?>(
      $"SELECT setting_value FROM {SCHEMA}.wh_settings WHERE setting_key = 'debug_mode'", null)).IsEqualTo("true");
    await coordinator.SyncRetentionSettingsAsync([RetentionSettingsBridge.DebugModeSetting(false)]);
    await Assert.That(await _scalarAsync<string?>(
      $"SELECT setting_value FROM {SCHEMA}.wh_settings WHERE setting_key = 'debug_mode'", null)).IsEqualTo("false");

    var results = await coordinator.PerformMaintenanceAsync();
    await Assert.That(results).IsNotEmpty();
  }

  [Test]
  public async Task RowRetention_RunsInTheConfiguredSchemaAsync() {
    // The Dapper driver does not write the perspective registry itself, so the row a host's perspective would have
    // is seeded here, with its table, in the configured schema.
    const string TABLE = "wh_per_named_retained";
    const string ANNOUNCER = "wh_per_named_announcer";
    const string CLR = "Named.Retained.Model";
    await _executeAsync($@"
      CREATE TABLE {SCHEMA}.{TABLE} (
        id UUID NOT NULL PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ, expires_at TIMESTAMPTZ, version INTEGER NOT NULL);
      CREATE TABLE {SCHEMA}.{ANNOUNCER} (id UUID NOT NULL PRIMARY KEY);
      INSERT INTO {SCHEMA}.wh_perspective_registry (clr_type_name, table_name, schema_json, schema_hash, service_name)
      VALUES (@c, @t, '{{}}'::jsonb, 'h', 'svc');", new { c = CLR, t = TABLE });
    var expired = Guid.CreateVersion7();
    var kept = Guid.CreateVersion7();
    foreach (var (id, hours) in new[] { (expired, 48), (kept, 0) }) {
      await _executeAsync($@"
        INSERT INTO {SCHEMA}.{TABLE} (id, data, metadata, scope, created_at, updated_at, version)
        VALUES (@id, '{{}}'::jsonb, '{{}}'::jsonb, '{{}}'::jsonb, NOW() - make_interval(hours => @h), NOW() - make_interval(hours => @h), 1)",
        new { id, h = hours });
    }
    var coordinator = _build();
    var keptRef = new Whizbang.Core.Lifecycle.PerspectiveRowRef(TABLE, kept);

    await coordinator.SyncPerspectiveRetentionAsync([new PerspectiveRetentionDeclaration(CLR, true, 3600, null, 5, "u")]);
    await Assert.That(await coordinator.CountPerspectiveRetentionBacklogAsync(CLR)).IsEqualTo(1L);
    await Assert.That(await coordinator.GetPerspectiveRowsAboutToReapAsync([CLR])).IsEmpty()
      .Because("the adoption gate withholds an unacknowledged perspective from the collect phase too");
    await Assert.That((await coordinator.AdoptEnrolledPerspectiveRetentionAsync()).Single().Backlog).IsEqualTo(1L);
    await coordinator.AcknowledgeRetentionEnforcementAsync(CLR);
    await Assert.That((await coordinator.GetPerspectiveRowsAboutToReapAsync([CLR])).Single().RowId).IsEqualTo(expired);
    await Assert.That((await coordinator.ReapEnrolledPerspectiveRowsAsync()).RowsAffected).IsEqualTo(1);
    await Assert.That(await coordinator.TryClaimRowCapSweepAsync(TimeSpan.FromHours(1))).IsTrue();
    await Assert.That((await coordinator.ReapPerspectiveRowCapsAsync()).RowsAffected).IsEqualTo(0);

    await Assert.That((await coordinator.DrainRowEvictionJournalAsync()).Single().RowId).IsEqualTo(expired);
    await coordinator.RequeueRowEvictionsAsync([keptRef]);
    await Assert.That(await _countAsync("wh_row_eviction_journal", "row_id = @r", new { r = kept })).IsEqualTo(1L);
    await Assert.That((await coordinator.GetPerspectiveTableNamesAsync([CLR])).Single().TableName).IsEqualTo(TABLE);
    await Assert.That((await coordinator.GetPerspectiveRowsByIdsAsync(CLR, TABLE, [kept])).Single().RowId).IsEqualTo(kept);

    await coordinator.HoldPerspectiveRowDestructionAsync([keptRef], _heldUntil);
    await Assert.That(await coordinator.RecordPerspectiveRowDestructionFailureAsync(
      [keptRef], TimeSpan.FromMinutes(1), 3, Whizbang.Core.Lifecycle.OnDestroyFailure.RetryThenKeep)).IsEqualTo(1);
    await Assert.That(await coordinator.CascadeDeletePerspectiveRowsAsync(TABLE, [kept])).IsEqualTo(0);
    await coordinator.ReleasePerspectiveRowHoldsAsync([keptRef]);
    await Assert.That(await _countAsync("wh_perspective_row_hold", "row_id = @r", new { r = kept })).IsEqualTo(0L);
    await Assert.That(await coordinator.ReconcileFollowerPresenceAsync(TABLE, [ANNOUNCER], [kept])).IsEqualTo(1)
      .Because("the announcer is looked up in the configured schema too; it lacks the row, so the unheld row goes");
    await _assertNoMissedRingsAsync();
  }

  [Test]
  public async Task WithNoSchemaGiven_TheConnectionStringsSearchPathConfiguresItAsync() {
    var configured = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { SearchPath = $"{SCHEMA}, public" }.ConnectionString;
    var coordinator = new DapperWorkCoordinator(configured, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), _log);
    var instanceId = Guid.CreateVersion7();

    await coordinator.RecordHeartbeatAsync(new HeartbeatRequest(instanceId, SERVICE, HOST, 7));

    await Assert.That(await _countAsync("wh_service_instances", "instance_id = @id", new { id = instanceId })).IsEqualTo(1L);
  }

  [Test]
  public async Task WithNothingConfigured_TheServersDefaultIsAskedOnceAndKeptAsync() {
    // A default set on the database (or the role) is what the server applies to every session, pooled ones included.
    await _executeAsync($"ALTER DATABASE \"{_database.Name}\" SET search_path = {SCHEMA}", new { });
    // Unpooled, so every call is a new session that reads the database's current default.
    var unpooled = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Pooling = false }.ConnectionString;
    var coordinator = new DapperWorkCoordinator(unpooled, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(), _log);
    await coordinator.SyncDebugRetentionSettingAsync(debugMode: true);

    await _executeAsync($"ALTER DATABASE \"{_database.Name}\" RESET search_path", new { });
    await coordinator.SyncDebugRetentionSettingAsync(debugMode: false);

    await Assert.That(await _scalarAsync<string?>(
      $"SELECT setting_value FROM {SCHEMA}.wh_settings WHERE setting_key = 'debug_mode'", null)).IsEqualTo("false")
      .Because("the schema the first call resolved is kept, so a later session that resolves elsewhere still lands here");
  }

  [Test]
  public async Task ASessionThatResolvesNoSchema_IsTakenAsPublicAsync() {
    var nowhere = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { SearchPath = "no_such_schema" }.ConnectionString;
    await using var connection = new NpgsqlConnection(nowhere);
    await connection.OpenAsync();

    await Assert.That(await DapperCoordinatorSchema.CurrentAsync(connection, CancellationToken.None)).IsEqualTo("public");
  }

  private static OutboxMessage _outbox(Guid messageId, Guid streamId) => new() {
    MessageId = messageId,
    Destination = "named-schema-topic",
    Envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonDocument.Parse("{\"k\":1}").RootElement, []),
    EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.NamedSchema, Test]], Whizbang.Core",
    MessageType = MESSAGE_TYPE,
    Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
    StreamId = streamId,
    IsEvent = false,
  };

  private static InboxMessage _inbox(Guid messageId, Guid streamId) => new() {
    MessageId = messageId,
    HandlerName = "NamedSchemaHandler",
    Envelope = new MessageEnvelope<JsonElement>(MessageId.From(messageId), JsonDocument.Parse("{\"p\":1}").RootElement, []),
    EnvelopeType = "Whizbang.Core.Observability.MessageEnvelope`1[[Test.NamedSchema, Test]], Whizbang.Core",
    MessageType = MESSAGE_TYPE,
    Metadata = new EnvelopeMetadata { MessageId = MessageId.From(messageId), Hops = [] },
    StreamId = streamId,
    IsEvent = true,
  };

  private static HandlerCommitRequest _commit(Guid instanceId, Guid inboxId) => new(
    HandlerId: Guid.CreateVersion7(), InstanceId: instanceId, ServiceName: SERVICE, HostName: HOST, ProcessId: 7,
    PartitionCount: PARTITIONS, InboxCompletion: new HandlerInboxCompletion(inboxId, Status: 2));

  private static PerspectiveCursorCompletion _completion(Guid streamId, Guid eventId) => new() {
    StreamId = streamId,
    PerspectiveName = PERSPECTIVE,
    LastEventId = eventId,
    ProcessedEventIds = [eventId],
    Status = PerspectiveProcessingStatus.Completed,
  };

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private async Task _executeAsync(string sql, object parameters) {
    await using var connection = await _openAsync();
    await connection.ExecuteAsync(sql, parameters);
  }

  private async Task<T> _scalarAsync<T>(string sql, object? parameters) {
    await using var connection = await _openAsync();
    return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
  }

  private Task<long> _countAsync(string table, string where, object parameters) =>
    _scalarAsync<long>($"SELECT count(*) FROM {SCHEMA}.{table} WHERE {where}", parameters);

  private async Task _assertNoMissedRingsAsync() =>
    await Assert.That(_log.Warnings).IsEmpty()
      .Because("a doorbell ring is logged, not thrown, when its function is not where the statement looked for it");

  /// <summary>Collects the coordinator's warnings, which is where a failed doorbell ring is reported.</summary>
  private sealed class WarningLog : ILogger<DapperWorkCoordinator> {
    public ConcurrentQueue<string> Warnings { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (IsEnabled(logLevel)) {
        Warnings.Enqueue(formatter(state, exception) + " " + exception?.Message);
      }
    }
  }
}
