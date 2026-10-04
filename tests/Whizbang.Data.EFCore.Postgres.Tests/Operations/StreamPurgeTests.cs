using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.Operations;

/// <summary>
/// The operator stream purge (#1030) against a real database: <c>wh_purge_streams</c> (migration 180) through
/// <see cref="PostgresStreamPurger"/>. A purge removes every row the listed streams own, in every table keyed by
/// them, and nothing of any other stream; a dry run reports the same counts and changes nothing; each batch is one
/// transaction that takes the existing PublishOnceAsync claim, so a batch runs once; and the stream stays purged.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/180_StreamPurge.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresStreamPurger.cs</code-under-test>
/// <docs>operations/infrastructure/purging-streams</docs>
[Category("Integration")]
[Category("Shard4")]
public class StreamPurgeTests : EFCoreTestBase {
  private const string EVENT_TYPE = "Purge.Tests.SomethingHappened";
  private const string PERSPECTIVE = "Purge.Tests.SomethingPerspective";
  private const string PERSPECTIVE_TABLE = "wh_per_action_test";

  /// <summary>Every table a stream owns rows in, as the purge reports it.</summary>
  private static readonly string[] _streamTables = [
    "wh_outbox", "wh_inbox_state", "wh_inbox", "wh_perspective_events", "wh_active_streams",
    "wh_message_deduplication", "wh_receptor_processing", "wh_perspective_cursors", "wh_perspective_snapshots",
    "wh_perspective_applied", PERSPECTIVE_TABLE, "wh_perspective_row_hold", "wh_row_eviction_journal",
    "wh_stream_digests", "wh_integrity_ledger", "wh_event_body", "wh_event_destruction_hold",
    "wh_lifecycle_completions", "wh_event_archive", "wh_apply_fold_watermarks", "wh_event_store",
  ];

  [Test]
  public async Task Purge_RemovesEveryRowOfTheStreams_AndLeavesOthersAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var doomed = Guid.CreateVersion7();
    var kept = Guid.CreateVersion7();
    await _seedStreamAsync(ctx, conn, doomed);
    await _seedStreamAsync(ctx, conn, kept);
    var before = await _countsAsync(conn, doomed);
    foreach (var table in _streamTables) {
      await Assert.That(before[table]).IsGreaterThan(0).Because($"the seed must reach {table}, or the purge of it proves nothing");
    }

    var report = await _purger().PurgeAsync(new StreamPurgeRequest {
      StreamIds = [doomed],
      RequestedBy = "operator-1",
      Reason = "created by a faulty replay",
    });

    await Assert.That(report.Batches).Count().IsEqualTo(1);
    await Assert.That(report.Batches[0].Ran).IsTrue();
    foreach (var table in _streamTables) {
      await Assert.That(report.Totals[table]).IsEqualTo(before[table]).Because($"the report counts what {table} lost");
    }
    var after = await _countsAsync(conn, doomed);
    foreach (var table in _streamTables) {
      await Assert.That(after[table]).IsEqualTo(0).Because($"{table} keeps no row of the purged stream");
    }
    var untouched = await _countsAsync(conn, kept);
    foreach (var table in _streamTables) {
      await Assert.That(untouched[table]).IsEqualTo(before[table]).Because($"{table} keeps every row of the other stream");
    }

    await Assert.That(await _scalarAsync<string>(conn,
        "SELECT perspective_name FROM wh_stream_purge_markers WHERE stream_id = $1 AND purge_id = $2", doomed, report.PurgeId))
      .IsEqualTo(PerspectivePurgeMarkers.ALL_PERSPECTIVES).Because("an operator purge marks the stream for every perspective");
    await using var audit = new NpgsqlCommand(
      "SELECT requested_by, reason, stream_ids, row_counts::text FROM wh_stream_purge_audit WHERE purge_id = $1 AND batch_index = 0", conn);
    audit.Parameters.AddWithValue(report.PurgeId);
    await using (var reader = await audit.ExecuteReaderAsync()) {
      await Assert.That(await reader.ReadAsync()).IsTrue().Because("each committed batch is audited");
      await Assert.That(reader.GetString(0)).IsEqualTo("operator-1");
      await Assert.That(reader.GetString(1)).IsEqualTo("created by a faulty replay");
      await Assert.That(((Guid[])reader.GetValue(2)).Single()).IsEqualTo(doomed);
      using var counts = JsonDocument.Parse(reader.GetString(3));
      await Assert.That(counts.RootElement.GetProperty("wh_event_store").GetInt64()).IsEqualTo(before["wh_event_store"]);
      await Assert.That(counts.RootElement.GetProperty("wh_stream_purge_markers").GetInt64()).IsEqualTo(1);
    }
    await Assert.That(await _scalarAsync<long>(conn,
        "SELECT count(*) FROM wh_unique_emission_claims WHERE claim_key = $1", PostgresStreamPurger.ClaimKey(report.PurgeId, 0)))
      .IsEqualTo(1).Because("the batch took the PublishOnceAsync claim in its own transaction");
    await Assert.That(report.Format()).Contains("Purged 1 stream(s) in 1 batch(es)");
  }

  [Test]
  public async Task DryRun_CountsWhatWouldGo_AndChangesNothingAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    await _seedStreamAsync(ctx, conn, stream);
    var before = await _countsAsync(conn, stream);

    var report = await _purger().PurgeAsync(new StreamPurgeRequest {
      StreamIds = [stream],
      RequestedBy = "operator-1",
      Reason = "checking first",
      DryRun = true,
    });

    foreach (var table in _streamTables) {
      await Assert.That(report.Totals[table]).IsEqualTo(before[table]).Because($"the dry run counts what {table} would lose");
    }
    await Assert.That(report.Totals["wh_stream_purge_markers"]).IsEqualTo(1);
    var after = await _countsAsync(conn, stream);
    foreach (var table in _streamTables) {
      await Assert.That(after[table]).IsEqualTo(before[table]).Because($"a dry run leaves {table} alone");
    }
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_stream_purge_markers")).IsEqualTo(0);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_stream_purge_audit")).IsEqualTo(0);
    await Assert.That(await _scalarAsync<long>(conn,
        "SELECT count(*) FROM wh_unique_emission_claims WHERE claim_key LIKE $1", PostgresStreamPurger.CLAIM_KEY_PREFIX + "%"))
      .IsEqualTo(0).Because("a dry run claims nothing");
    await Assert.That(report.Format()).Contains("Dry run: would purge 1 stream(s)");
  }

  [Test]
  public async Task Purge_OfTheEmptyStreamId_IsRefusedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    await using var call = new NpgsqlCommand("SELECT * FROM wh_purge_streams($1, false, $2, 0, 'operator-1', 'x')", conn);
    call.Parameters.Add(new NpgsqlParameter { Value = new[] { Guid.Empty }, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
    call.Parameters.AddWithValue(Guid.CreateVersion7());

    var refused = await Assert.ThrowsAsync<PostgresException>(async () => await call.ExecuteNonQueryAsync());

    await Assert.That(refused!.MessageText).Contains("the empty stream id cannot be purged");
  }

  [Test]
  public async Task Batches_AreClaimedOnce_AndARerunSkipsWhatCommittedAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var streams = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
    foreach (var stream in streams) {
      await _appendEventAsync(conn, stream);
    }
    var request = new StreamPurgeRequest {
      StreamIds = streams,
      RequestedBy = "operator-1",
      Reason = "batched",
      BatchSize = 2,
    };
    // Another instance already holds the second batch's claim.
    await using (var claim = new NpgsqlCommand(
        "INSERT INTO wh_unique_emission_claims (claim_key, claimed_by_event_id) VALUES ($1, $2)", conn)) {
      claim.Parameters.AddWithValue(PostgresStreamPurger.ClaimKey(request.PurgeId, 1));
      claim.Parameters.AddWithValue(request.PurgeId);
      await claim.ExecuteNonQueryAsync();
    }

    var log = new ListLogger();
    var first = await _purger(log).PurgeAsync(request);

    await Assert.That(first.Batches.Select(b => b.Ran).ToArray()).IsEquivalentTo(_ranThenSkipped);
    await Assert.That(first.Batches[1].RowsByTable).IsEmpty();
    var secondBatchStream = request.Batches()[1].Single();
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_event_store WHERE stream_id = $1", secondBatchStream))
      .IsEqualTo(1).Because("the batch another instance claimed is not run here");
    await Assert.That(first.Totals["wh_event_store"]).IsEqualTo(2);
    await Assert.That(first.Format()).Contains("Skipped 1 batch(es) already claimed");
    await Assert.That(log.Messages).Contains(m => m.Contains("batch 0: 2 stream(s), 2 event(s)", StringComparison.Ordinal))
      .Because("each batch is logged with who asked and why");
    await Assert.That(log.Messages).Contains(m => m.Contains("batch 1 is already claimed", StringComparison.Ordinal));

    var rerun = await _purger().PurgeAsync(request);

    await Assert.That(rerun.Batches.All(b => !b.Ran)).IsTrue()
      .Because("a rerun of the same purge skips the batch it committed and the one still claimed elsewhere");
  }

  [Test]
  public async Task PurgedStream_ALaterEvent_IsSkippedByEveryPerspectiveAsync() {
    await using var ctx = CreateDbContext();
    var conn = await _openAsync(ctx);
    var stream = Guid.CreateVersion7();
    await _purger().PurgeAsync(new StreamPurgeRequest {
      StreamIds = [stream],
      RequestedBy = "operator-1",
      Reason = "never existed upstream",
    });

    // A late event for the purged stream reaches a perspective after the purge.
    var eventStore = new InMemoryEventStore();
    await eventStore.AppendAsync(stream, new MessageEnvelope<ActionTestUpdatedEvent> {
      MessageId = MessageId.New(),
      Payload = new ActionTestUpdatedEvent { StreamId = stream, NewValue = 7 },
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = [],
    });
    await using var storeContext = CreateDbContext();
    var rows = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, PERSPECTIVE_TABLE);
    await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
    var runner = PurgeMarkerRunner.Create(eventStore, rows, new PostgresPerspectivePurgeMarkerStore(dataSource.OpenConnectionAsync));

    await runner.RunAsync(stream, "action_test", null, CancellationToken.None);

    await Assert.That(await _scalarAsync<long>(conn, $"SELECT count(*) FROM {PERSPECTIVE_TABLE} WHERE id = $1", stream))
      .IsEqualTo(0).Because("an operator purge keeps the stream purged for every perspective");
  }

  [Test]
  public async Task Driver_RegistersThePurgeMarkersAndTheStreamPurgerAsync() {
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    // The driver reads the model's default schema to qualify the purge tables, so the context's model must
    // build: an empty model with a schema does, and nothing connects.
    services.AddDbContext<PurgeRegistrationDbContext>(options => options.UseNpgsql("Host=localhost;Database=unused"));
    services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=unused"));
    var selector = new WhizbangPerspectiveBuilder(services).WithEFCore<PurgeRegistrationDbContext>();
    _ = selector.WithDriver.Postgres;

    await using var sp = services.BuildServiceProvider();

    await Assert.That(sp.GetRequiredService<IPerspectivePurgeMarkerStore>()).IsTypeOf<PostgresPerspectivePurgeMarkerStore>();
    await Assert.That(sp.GetRequiredService<IStreamPurger>()).IsTypeOf<PostgresStreamPurger>();
    await Assert.That(sp.GetService<IPerspectiveTableSwapper>()).IsTypeOf<Whizbang.Data.Postgres.Perspectives.PostgresPerspectiveTableSwapper>()
      .Because("the driver registers the blue-green rebuild's swapper over an independent connection");
  }

  private sealed class PurgeRegistrationDbContext(DbContextOptions<PurgeRegistrationDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema("purge_registration");
  }

  // -------------------------------------------------------------------------------------------

  private static readonly bool[] _ranThenSkipped = [true, false];

  private readonly Dictionary<Guid, Guid[]> _eventIdsByStream = [];
  private readonly Guid _instance = Guid.CreateVersion7();

  private PostgresStreamPurger _purger(ListLogger? logger = null) {
    var dataSource = NpgsqlDataSource.Create(ConnectionString);
    return new PostgresStreamPurger(dataSource.OpenConnectionAsync, logger: logger);
  }

  /// <summary>Records what the purger logs at Information and above.</summary>
  private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<PostgresStreamPurger> {
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
  }

  private static async Task<NpgsqlConnection> _openAsync(DbContext ctx) {
    var connection = ctx.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open) {
      await connection.OpenAsync();
    }
    return (NpgsqlConnection)connection;
  }

  /// <summary>
  /// Gives the stream a row in every table it can own one in: through the real inbound path where there is one
  /// (inbox, claim into the event store and perspective work, completion into the cursor and the applied ledger),
  /// directly where the row is written by machinery a test cannot drive cheaply.
  /// </summary>
  private async Task _seedStreamAsync(WorkCoordinationDbContext ctx, NpgsqlConnection conn, Guid stream) {
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(ctx, JsonContextRegistry.CreateCombinedOptions());
    await _executeAsync(conn, """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), $1, 'perspective', $2, 'service-b', $1, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """, EVENT_TYPE, PERSPECTIVE);

    // Inbound: an event from another service lands in the inbox; the claim stores it and creates perspective work.
    var eventId = Guid.CreateVersion7();
    var envelope = new MessageEnvelope<JsonElement>(MessageId.From(eventId), JsonDocument.Parse("{\"p\":1}").RootElement, []);
    await coordinator.StoreInboxMessagesAsync([new InboxMessage {
      MessageId = eventId,
      HandlerName = "PurgeHandler",
      Envelope = envelope,
      EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{EVENT_TYPE}]], Whizbang.Core",
      MessageType = EVENT_TYPE,
      StreamId = stream,
      IsEvent = true,
      Metadata = new EnvelopeMetadata { MessageId = MessageId.From(eventId), Hops = [] },
    }], partitionCount: 100);
    // One instance claims for every seeded stream: a second instance would find the partitions owned.
    var instance = _instance;
    await _executeAsync(conn, """
      INSERT INTO wh_service_instances (instance_id, service_name, host_name, process_id, last_heartbeat_at, started_at)
      VALUES ($1, 'service-b', 'test-host', 1, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """, instance);
    await _executeAsync(conn, "SELECT count(*) FROM claim_work($1, 'service-b', 'test-host', 1, 10, 100, 300, 0.5, 10, FALSE, NULL)", instance);

    // The work row survives the completion below only for a second event; complete the first, keep the second's.
    var workId = await _scalarAsync<Guid>(conn, "SELECT event_work_id FROM wh_perspective_events WHERE event_id = $1", eventId);
    await coordinator.CompletePerspectiveAsync(cursors: [], eventWorkIds: [workId], debugMode: false);
    // The cursor points at the stream's event, so the purge must remove it before the events (the FK is RESTRICT).
    await _executeAsync(conn, """
      INSERT INTO wh_perspective_cursors (stream_id, perspective_name, last_event_id, status, processed_at)
      VALUES ($1, $2, $3, 2, NOW())
      """, stream, PERSPECTIVE, eventId);
    await _executeAsync(conn, """
      INSERT INTO wh_perspective_events (event_work_id, stream_id, perspective_name, event_id)
      VALUES (gen_random_uuid(), $1, $2, $3)
      """, stream, PERSPECTIVE + ".Second", eventId);
    await _executeAsync(conn, "INSERT INTO wh_active_streams (stream_id, partition_number) VALUES ($1, 0) ON CONFLICT DO NOTHING", stream);

    // Outbound: the stream emitted a message.
    await coordinator.StoreOutboxMessagesAsync([new OutboxMessage {
      MessageId = Guid.CreateVersion7(),
      Destination = "topic",
      Envelope = new MessageEnvelope<JsonElement>(MessageId.New(), JsonDocument.Parse("{}").RootElement, []),
      Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
      EnvelopeType = typeof(MessageEnvelope<JsonElement>).AssemblyQualifiedName!,
      MessageType = "Purge.Tests.Outbound",
      StreamId = stream,
      IsEvent = false,
    }], partitionCount: 100);

    // The perspective's row, its retention hold and an eviction journal entry.
    var rows = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx, PERSPECTIVE_TABLE);
    await rows.UpsertAsync(stream, new ActionTestModel { Id = stream, Name = "row", Value = 1 });
    await _executeAsync(conn, """
      INSERT INTO wh_perspective_registry (id, clr_type_name, table_name, schema_json, schema_hash, service_name, created_at, updated_at)
      VALUES (gen_random_uuid(), $1, $2, '{}'::jsonb, 'h', 'service-b', NOW(), NOW())
      ON CONFLICT DO NOTHING
      """, typeof(ActionTestPerspective).FullName!, PERSPECTIVE_TABLE);
    await _executeAsync(conn, "INSERT INTO wh_perspective_row_hold (table_name, row_id, hold_until) VALUES ($1, $2, NOW() + INTERVAL '1 day')", PERSPECTIVE_TABLE, stream);
    await _executeAsync(conn, "INSERT INTO wh_row_eviction_journal (table_name, row_id) VALUES ('wh_per_other', $1)", stream);

    // Bookkeeping written by machinery outside this test's reach.
    await _executeAsync(conn, "INSERT INTO wh_message_deduplication (message_id, first_seen_at) VALUES ($1, NOW()) ON CONFLICT DO NOTHING", eventId);
    await _executeAsync(conn, """
      INSERT INTO wh_receptor_processing (id, event_id, receptor_name, stream_id, status, attempts, started_at)
      VALUES (gen_random_uuid(), $1, 'Receptor', $2, 0, 0, NOW())
      """, eventId, stream);
    await _executeAsync(conn, """
      INSERT INTO wh_perspective_snapshots (stream_id, perspective_name, snapshot_event_id, snapshot_data, sequence_number)
      VALUES ($1, $2, $3, '{}'::jsonb, 1)
      """, stream, PERSPECTIVE, eventId);
    await _executeAsync(conn, """
      INSERT INTO wh_stream_digests (event_type, stream_id, digest_lo, digest_hi, event_count)
      VALUES ($1, $2, 1, 1, 1) ON CONFLICT DO NOTHING
      """, EVENT_TYPE, stream);
    await _executeAsync(conn, """
      INSERT INTO wh_integrity_ledger (origin_service_id, event_type, stream_id, origin_lo, origin_hi, local_lo, local_hi)
      VALUES (gen_random_uuid(), $1, $2, 1, 1, 1, 1)
      """, EVENT_TYPE, stream);
    await _executeAsync(conn, "INSERT INTO wh_event_destruction_hold (event_id, hold_until) VALUES ($1, NOW() + INTERVAL '1 day')", eventId);
    await _executeAsync(conn, "INSERT INTO wh_lifecycle_completions (event_id, instance_id) VALUES ($1, $2)", eventId, instance);
    await _executeAsync(conn, """
      INSERT INTO wh_event_archive (event_id, stream_id, event_type, version)
      VALUES (gen_random_uuid(), $1, $2, 0)
      """, stream, EVENT_TYPE);
    await _executeAsync(conn, "INSERT INTO wh_apply_fold_watermarks (stream_id) VALUES ($1)", stream);
  }

  /// <summary>One event in the store, written directly, with nothing else.</summary>
  private static async Task _appendEventAsync(NpgsqlConnection conn, Guid stream) {
    await _executeAsync(conn, """
      INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at)
      VALUES ($1, $2, $2, 'Purge.Tests', $3, 1, NOW())
      """, Guid.CreateVersion7(), stream, EVENT_TYPE);
  }

  /// <summary>Rows the stream owns per table, keyed the way the purge reaches them.</summary>
  private async Task<Dictionary<string, long>> _countsAsync(NpgsqlConnection conn, Guid stream) {
    // Captured once per stream, so the event-keyed tables are counted by the same ids after the events are gone.
    if (!_eventIdsByStream.TryGetValue(stream, out var eventIds)) {
      await using var read = new NpgsqlCommand("SELECT event_id FROM wh_event_store WHERE stream_id = $1", conn);
      read.Parameters.AddWithValue(stream);
      var ids = new List<Guid>();
      await using (var reader = await read.ExecuteReaderAsync()) {
        while (await reader.ReadAsync()) {
          ids.Add(reader.GetGuid(0));
        }
      }
      eventIds = [.. ids];
      _eventIdsByStream[stream] = eventIds;
    }
    const string BY_EVENT = "= ANY($1)";
    var counts = new Dictionary<string, long>(StringComparer.Ordinal);
    foreach (var table in new[] {
        "wh_outbox", "wh_inbox", "wh_perspective_events", "wh_active_streams", "wh_receptor_processing",
        "wh_perspective_cursors", "wh_perspective_snapshots", "wh_perspective_applied", "wh_stream_digests",
        "wh_integrity_ledger", "wh_event_archive", "wh_apply_fold_watermarks", "wh_event_store" }) {
      counts[table] = await _scalarAsync<long>(conn, $"SELECT count(*) FROM {table} WHERE stream_id = $1", stream);
    }
    counts["wh_inbox_state"] = await _scalarAsync<long>(conn,
      "SELECT count(*) FROM wh_inbox_state s JOIN wh_inbox i ON i.message_id = s.message_id WHERE i.stream_id = $1", stream);
    counts["wh_message_deduplication"] = await _scalarAsync<long>(conn,
      $"SELECT count(*) FROM wh_message_deduplication WHERE message_id {BY_EVENT}", eventIds);
    foreach (var table in new[] { "wh_event_body", "wh_event_destruction_hold", "wh_lifecycle_completions" }) {
      counts[table] = await _scalarAsync<long>(conn, $"SELECT count(*) FROM {table} WHERE event_id {BY_EVENT}", eventIds);
    }
    counts[PERSPECTIVE_TABLE] = await _scalarAsync<long>(conn, $"SELECT count(*) FROM {PERSPECTIVE_TABLE} WHERE id = $1", stream);
    counts["wh_perspective_row_hold"] = await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_perspective_row_hold WHERE row_id = $1", stream);
    counts["wh_row_eviction_journal"] = await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_row_eviction_journal WHERE row_id = $1", stream);
    return counts;
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql, params object[] args) {
    await using var command = new NpgsqlCommand(sql, conn);
    foreach (var arg in args) {
      command.Parameters.AddWithValue(arg);
    }
    await command.ExecuteNonQueryAsync();
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql, params object[] args) {
    await using var command = new NpgsqlCommand(sql, conn);
    foreach (var arg in args) {
      command.Parameters.AddWithValue(arg);
    }
    return (T)(await command.ExecuteScalarAsync())!;
  }
}
