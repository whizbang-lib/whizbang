// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The EF Core driver's maintenance step that drives the stream lifecycle backfill (#1732): bounded batches, each its
/// own transaction, outside the migration; finished once, then idle.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#the-one-time-backfill</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/StreamLifecycleBackfillMaintenanceStep.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/StreamLifecycleBackfill.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StreamLifecycleBackfillMaintenanceStepTests : EFCoreTestBase {
  private const string VERSION_KEY = "stream_lifecycle_backfill_version";
  private const string CURSOR_KEY = "stream_lifecycle_backfill_cursor";

  private static readonly DateTimeOffset _t0 = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task Run_OnAnUpgradedStore_SeedsEveryStreamInBatches_AndRecordsItIsDoneAsync() {
    await using var conn = await _openAsync();
    await _seedStreamsAsync(conn, 5);
    await _restartAsync(conn);
    var logger = new ListLogger();

    await _runAsync(new StreamLifecycleBackfillMaintenanceStep(typeof(WorkCoordinationDbContext), logger, batchSize: 2, maxRoundsPerRun: 10));

    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_stream_lifecycle")).IsEqualTo(5L);
    await Assert.That(await _settingAsync(conn, VERSION_KEY)).IsEqualTo("1");
    await Assert.That(logger.Messages).Contains(m => m.Contains("5 streams", StringComparison.Ordinal))
      .Because("finishing is reported once, with how many streams the run visited");
  }

  [Test]
  public async Task Run_StopsAtItsRoundLimit_AndTheNextRunContinuesWhereItStoppedAsync() {
    await using var conn = await _openAsync();
    await _seedStreamsAsync(conn, 3);
    await _restartAsync(conn);
    var step = new StreamLifecycleBackfillMaintenanceStep(typeof(WorkCoordinationDbContext), batchSize: 1, maxRoundsPerRun: 2);

    await _runAsync(step);
    var afterFirst = await _countAsync(conn, "SELECT count(*) FROM wh_stream_lifecycle");
    var markerAfterFirst = await _settingAsync(conn, VERSION_KEY);
    await _runAsync(step);

    await Assert.That(afterFirst).IsEqualTo(2L).Because("two rounds of one stream each, then the run yields the cycle");
    await Assert.That(markerAfterFirst).IsEqualTo("0");
    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_stream_lifecycle")).IsEqualTo(3L);
    await Assert.That(await _settingAsync(conn, VERSION_KEY)).IsEqualTo("1");
  }

  [Test]
  public async Task Run_OnceDone_WritesNothingAsync() {
    await using var conn = await _openAsync();
    await _seedStreamsAsync(conn, 1);
    var logger = new ListLogger();

    await _runAsync(new StreamLifecycleBackfillMaintenanceStep(typeof(WorkCoordinationDbContext), logger));

    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_stream_lifecycle")).IsEqualTo(0L)
      .Because("a new database records the backfill as done, and a done backfill runs nothing");
    await Assert.That(logger.Messages).IsEmpty().Because("an idle step says nothing on every cycle");
  }

  [Test]
  public async Task Run_WhileAnotherInstanceHoldsTheCursor_StepsAsideAsync() {
    await using var conn = await _openAsync();
    await using var other = await _openAsync();
    await _seedStreamsAsync(conn, 2);
    await _restartAsync(conn);
    var logger = new ListLogger();

    await using var tx = await other.BeginTransactionAsync();
    await using (var hold = other.CreateCommand()) {
      hold.CommandText = $"SELECT 1 FROM wh_settings WHERE setting_key = '{CURSOR_KEY}' FOR UPDATE";
      _ = await hold.ExecuteScalarAsync();
    }
    await _runAsync(new StreamLifecycleBackfillMaintenanceStep(typeof(WorkCoordinationDbContext), logger));
    await tx.RollbackAsync();

    await Assert.That(await _countAsync(conn, "SELECT count(*) FROM wh_stream_lifecycle")).IsEqualTo(0L);
    await Assert.That(logger.Messages).Contains(m => m.Contains("another instance", StringComparison.Ordinal));
  }

  [Test]
  public async Task TheStep_RefusesWhatItCannotRunAsync() {
    await Assert.That(() => new StreamLifecycleBackfillMaintenanceStep(null!)).Throws<ArgumentNullException>();
    await Assert.That(async () => await new StreamLifecycleBackfillMaintenanceStep(typeof(WorkCoordinationDbContext))
      .RunAsync(null!, CancellationToken.None)).Throws<ArgumentNullException>();
    await using var conn = await _openAsync();
    await Assert.That(async () => await StreamLifecycleBackfill.RunAsync(null!, "\"public\"", 1, 1, null, CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await StreamLifecycleBackfill.RunAsync(conn, " ", 1, 1, null, CancellationToken.None))
      .Throws<ArgumentException>();
    await Assert.That(async () => await StreamLifecycleBackfill.RunAsync(conn, "\"public\"", 0, 1, null, CancellationToken.None))
      .Throws<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task ThePostgresDriver_RegistersTheStepAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<WorkCoordinationDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<StreamLifecycleBackfillMaintenanceStep>().ToList();

    await Assert.That(steps.Count).IsEqualTo(1);
    await Assert.That(steps.Single().Name).IsEqualTo("stream-lifecycle-backfill");
  }

  // ===== Helpers =====

  private async Task<NpgsqlConnection> _openAsync() {
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return connection;
  }

  private async Task _runAsync(StreamLifecycleBackfillMaintenanceStep step) {
    await using var context = CreateDbContext();
    var services = new ServiceCollection();
    services.AddSingleton(context);
    await using var provider = services.BuildServiceProvider();
    await step.RunAsync(provider, CancellationToken.None);
  }

  /// <summary>One event per stream, written straight into the store as a release before the ledger wrote it.</summary>
  private static async Task _seedStreamsAsync(NpgsqlConnection conn, int count) {
    for (var i = 0; i < count; i++) {
      var id = (Guid)TrackedGuid.New();
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = """
        INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at, flags)
        VALUES (@id, @s, @s, 'Agg.Step', 'Agg.Step, Tests', 1, @at, 0);
        INSERT INTO wh_event_body (event_id, event_data, metadata) VALUES (@id, '{"a": 1}'::jsonb, '{}'::jsonb);
        """;
      cmd.Parameters.AddWithValue("id", id);
      cmd.Parameters.AddWithValue("s", (Guid)TrackedGuid.New());
      cmd.Parameters.AddWithValue("at", _t0.AddMinutes(i));
      _ = await cmd.ExecuteNonQueryAsync();
    }
  }

  private static async Task _restartAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
      UPDATE wh_settings SET setting_value = '0' WHERE setting_key = '{VERSION_KEY}';
      UPDATE wh_settings SET setting_value = '{Guid.Empty}' WHERE setting_key = '{CURSOR_KEY}';
      """;
    _ = await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<long> _countAsync(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<string?> _settingAsync(NpgsqlConnection conn, string key) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT setting_value FROM wh_settings WHERE setting_key = @k";
    cmd.Parameters.AddWithValue("k", key);
    return (string?)await cmd.ExecuteScalarAsync();
  }

  /// <summary>Records what the step logs.</summary>
  private sealed class ListLogger : ILogger<StreamLifecycleBackfillMaintenanceStep> {
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Messages.Add(formatter(state, exception));
  }
}
