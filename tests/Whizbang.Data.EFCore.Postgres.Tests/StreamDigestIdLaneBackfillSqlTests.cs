// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2106: the bounded fill of the id lanes of stream digests stored before 336, outside the migration
/// (<c>wh_backfill_stream_digest_id_lanes_batch</c>, driven by <see cref="StreamDigestIdLaneBackfill"/> from an
/// idle-gated maintenance step). It fills, resumes from a cursor, steps aside when another caller holds it, finishes
/// once, and never corrects a drifted bucket without reporting it: a Warning naming the bucket with its stored and
/// recomputed values, and the <c>stream_digest_drift_corrected</c> count.
/// </summary>
/// <docs>resilience/stream-integrity#one-digest-fold</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/StreamDigestIdLaneBackfill.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/StreamDigestIdLaneBackfillMaintenanceStep.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StreamDigestIdLaneBackfillSqlTests : EFCoreTestBase {
  private const string VERSION_KEY = "stream_digest_id_lanes_backfill_version";
  private const string CURSOR_KEY = "stream_digest_id_lanes_backfill_cursor";
  private const string TYPE = "Whizbang.Tests.IdLaneBackfillEvent, Whizbang.Tests";

  // ===== the batch function =====

  [Test]
  public async Task Backfill_FillsEveryStreamsIdLanes_AndRecordsThatItIsDoneAsync() {
    await using var conn = await _openAsync();
    var streams = await _storedBeforeTheLanesAsync(conn, 5);

    var batches = 0;
    while ((await _batchAsync(conn, limit: 2)).Visited > 0) {
      batches++;
    }

    await Assert.That(batches).IsEqualTo(3).Because("five streams at two a batch");
    await Assert.That(await _settingAsync(conn, VERSION_KEY)).IsEqualTo("1");
    await StoredDigestInvariant.AssertAgreeAsync(conn, streams);
  }

  [Test]
  public async Task Backfill_ADriftedBucket_IsReturnedWithBothValues_ThenCorrectedAsync() {
    await using var conn = await _openAsync();
    var streams = await _storedBeforeTheLanesAsync(conn, 1);
    var storedLo = await _scalarAsync<long>(conn, $"SELECT digest_lo FROM wh_stream_digests WHERE stream_id = '{streams[0]}'");
    await _executeAsync(conn, $"UPDATE wh_stream_digests SET digest_lo = digest_lo # 1, event_count = event_count + 1 WHERE stream_id = '{streams[0]}'");

    var batch = await _batchAsync(conn, limit: 10);

    var drift = batch.Rows.Single();
    await Assert.That(drift.Kind).IsEqualTo("drift_updated");
    await Assert.That(drift.StoredLo).IsEqualTo(storedLo ^ 1).Because("the report names what was stored");
    await Assert.That(drift.RecomputedLo).IsEqualTo(storedLo).Because("and what the events say");
    await StoredDigestInvariant.AssertAgreeAsync(conn, streams);
  }

  [Test]
  public async Task Backfill_StoppedAfterOneBatch_ResumesAfterItsCursorAsync() {
    await using var conn = await _openAsync();
    var streams = await _storedBeforeTheLanesAsync(conn, 3);

    var first = await _batchAsync(conn, limit: 1);

    await Assert.That(first.Visited).IsEqualTo(1);
    await Assert.That(await _settingAsync(conn, CURSOR_KEY)).IsEqualTo(streams.Order().First().ToString());
    await Assert.That(await _scalarAsync<long>(conn,
        $"SELECT count(*) FROM wh_stream_digests WHERE stream_id = ANY(ARRAY['{string.Join("','", streams)}']::uuid[]) AND id_lo IS NULL"))
      .IsEqualTo(2L).Because("only the first stream was visited");
    await Assert.That((await _batchAsync(conn, limit: 5)).Visited).IsEqualTo(2).Because("the next batch starts after the cursor");
  }

  [Test]
  public async Task Backfill_WhileAnotherCallerHoldsTheCursor_ReportsBusyAndWritesNothingAsync() {
    await using var conn = await _openAsync();
    await _storedBeforeTheLanesAsync(conn, 1);
    await using var other = await _openAsync();
    await using var tx = await other.BeginTransactionAsync();
    await _executeAsync(other, $"SELECT 1 FROM wh_settings WHERE setting_key = '{CURSOR_KEY}' FOR UPDATE");

    var busy = await _batchAsync(conn, limit: 10);

    await Assert.That(busy.Visited).IsEqualTo(-1);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_stream_digests WHERE id_lo IS NULL")).IsGreaterThan(0L);
  }

  [Test]
  public async Task Backfill_OnceDone_ReadsNothingAndChangesNothingAsync() {
    await using var conn = await _openAsync();
    await _storedBeforeTheLanesAsync(conn, 1);
    await _executeAsync(conn, $"UPDATE wh_settings SET setting_value = '1' WHERE setting_key = '{VERSION_KEY}'");

    var batch = await _batchAsync(conn, limit: 10);

    await Assert.That(batch.Visited).IsEqualTo(0);
    await Assert.That(await _scalarAsync<long>(conn, "SELECT count(*) FROM wh_stream_digests WHERE id_lo IS NULL")).IsGreaterThan(0L)
      .Because("a finished backfill is one keyed read");
  }

  [Test]
  public async Task Backfill_WithItsCursorRowMissing_StartsFromTheBeginningAsync() {
    await using var conn = await _openAsync();
    var streams = await _storedBeforeTheLanesAsync(conn, 2);
    await _executeAsync(conn, $"DELETE FROM wh_settings WHERE setting_key = '{CURSOR_KEY}'");

    while ((await _batchAsync(conn, limit: 5)).Visited > 0) {
      // runs to the end
    }

    await StoredDigestInvariant.AssertAgreeAsync(conn, streams);
  }

  // ===== the driver and the maintenance step =====

  [Test]
  public async Task Run_FillsInBatches_ReportsDriftAtWarningAndOnTheMeter_AndFinishesAsync() {
    await using var conn = await _openAsync();
    var streams = await _storedBeforeTheLanesAsync(conn, 3);
    var drifted = streams.Order().Last();
    await _executeAsync(conn, $"UPDATE wh_stream_digests SET digest_hi = digest_hi # 4 WHERE stream_id = '{drifted}'");
    var metrics = new StreamIntegrityMetrics(new WhizbangMetrics(
      meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));
    long corrected = 0;
    using var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (instrument.Name == "whizbang.stream_integrity.stream_digest_drift_corrected") {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref corrected, value));
    listener.Start();
    var logger = new ListLogger();

    var visited = await StreamDigestIdLaneBackfill.RunAsync(conn, "\"public\"", 2, 10, logger, metrics, CancellationToken.None);
    listener.RecordObservableInstruments();

    await Assert.That(visited).IsEqualTo(3L);
    await Assert.That(await _settingAsync(conn, VERSION_KEY)).IsEqualTo("1");
    var warning = logger.Entries.Single(e => e.Level == LogLevel.Warning);
    await Assert.That(warning.Message).Contains(drifted.ToString())
      .Because("the drifted bucket is named, so an operator can find what an unaccounted write path touched");
    await Assert.That(Interlocked.Read(ref corrected)).IsEqualTo(1L);
    await StoredDigestInvariant.AssertAgreeAsync(conn, streams);
  }

  [Test]
  public async Task Run_StopsAtItsRoundLimit_AndWhenBusy_StepsAsideAsync() {
    await using var conn = await _openAsync();
    await _storedBeforeTheLanesAsync(conn, 3);

    await Assert.That(await StreamDigestIdLaneBackfill.RunAsync(conn, "\"public\"", 1, 2, null, null, CancellationToken.None))
      .IsEqualTo(2L).Because("two rounds of one stream");
    await using var other = await _openAsync();
    await using var tx = await other.BeginTransactionAsync();
    await _executeAsync(other, $"SELECT 1 FROM wh_settings WHERE setting_key = '{CURSOR_KEY}' FOR UPDATE");
    await Assert.That(await StreamDigestIdLaneBackfill.RunAsync(conn, "\"public\"", 1, 5, null, null, CancellationToken.None))
      .IsEqualTo(0L).Because("another instance holds the cursor");
  }

  [Test]
  public async Task Run_RefusesWhatItCannotRunAsync() {
    await using var conn = await _openAsync();
    await Assert.That(async () => await StreamDigestIdLaneBackfill.RunAsync(null!, "\"public\"", 1, 1, null, null, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await StreamDigestIdLaneBackfill.RunAsync(conn, " ", 1, 1, null, null, CancellationToken.None))
      .Throws<ArgumentException>();
    await Assert.That(async () => await StreamDigestIdLaneBackfill.RunAsync(conn, "\"public\"", 0, 1, null, null, CancellationToken.None))
      .Throws<ArgumentOutOfRangeException>();
    await Assert.That(() => new StreamDigestIdLaneBackfillMaintenanceStep(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new StreamDigestIdLaneBackfillMaintenanceStep(typeof(WorkCoordinationDbContext))
      .RunAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task TheStep_RunsTheBackfill_WithTheServicesMetricsAsync() {
    await using var conn = await _openAsync();
    var streams = await _storedBeforeTheLanesAsync(conn, 2);
    await using var context = CreateDbContext();
    var services = new ServiceCollection();
    services.AddSingleton(context);
    await using var provider = services.BuildServiceProvider();
    var step = new StreamDigestIdLaneBackfillMaintenanceStep(typeof(WorkCoordinationDbContext));

    await step.RunAsync(provider, CancellationToken.None);

    await Assert.That(step.Name).IsEqualTo("stream-digest-id-lane-backfill");
    await StoredDigestInvariant.AssertAgreeAsync(conn, streams);
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    await Assert.That(scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<StreamDigestIdLaneBackfillMaintenanceStep>().Count())
      .IsEqualTo(1);
  }

  // ===== Helpers =====

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  /// <summary>
  /// Streams of two events each, folded by the write path and then stripped of their id lanes, as a store upgraded
  /// from before 336 holds them; the backfill restarted from the beginning.
  /// </summary>
  private static async Task<Guid[]> _storedBeforeTheLanesAsync(NpgsqlConnection conn, int count) {
    var streams = new Guid[count];
    for (var i = 0; i < count; i++) {
      streams[i] = TrackedGuid.New().Value;
      for (var v = 1; v <= 2; v++) {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
          SELECT wh_append_event(@id, @s, @s, 'Agg.IdLanes', @type, '{}'::jsonb, '{}'::jsonb, NULL, NOW() - INTERVAL '2 hours')
          """;
        cmd.Parameters.AddWithValue("id", TrackedGuid.New().Value);
        cmd.Parameters.AddWithValue("s", streams[i]);
        cmd.Parameters.AddWithValue("type", TYPE);
        _ = await cmd.ExecuteScalarAsync();
      }
    }
    await _executeAsync(conn, $"""
      UPDATE wh_stream_digests SET id_lo = NULL, id_hi = NULL WHERE stream_id = ANY(ARRAY['{string.Join("','", streams)}']::uuid[]);
      UPDATE wh_settings SET setting_value = '0' WHERE setting_key = '{VERSION_KEY}';
      UPDATE wh_settings SET setting_value = '{Guid.Empty}' WHERE setting_key = '{CURSOR_KEY}';
      """);
    return streams;
  }

  private sealed record BatchRow(string? Kind, Guid? Stream, long? StoredLo, long? RecomputedLo);

  private sealed record Batch(int Visited, List<BatchRow> Rows);

  private static async Task<Batch> _batchAsync(NpgsqlConnection conn, int limit) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT visited, kind, stream_id, stored_lo, recomputed_lo FROM wh_backfill_stream_digest_id_lanes_batch(@n)";
    cmd.Parameters.AddWithValue("n", limit);
    var visited = 0;
    var rows = new List<BatchRow>();
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) {
      visited = r.GetInt32(0);
      if (!r.IsDBNull(1)) {
        rows.Add(new BatchRow(r.GetString(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt64(4)));
      }
    }
    return new Batch(visited, rows.Where(x => x.Kind != "filled").ToList());
  }

  private static async Task _executeAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) {
      // drained
    }
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return (T)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<string?> _settingAsync(NpgsqlConnection conn, string key) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT setting_value FROM wh_settings WHERE setting_key = @k";
    cmd.Parameters.AddWithValue("k", key);
    return (string?)await cmd.ExecuteScalarAsync();
  }

  private sealed record LogEntry(LogLevel Level, string Message);

  private sealed class ListLogger : ILogger {
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
      Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
  }
}
