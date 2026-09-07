using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Targeted coverage for the debug-logging branches of <see cref="DapperPerspectiveSnapshotStore"/>
/// that no existing test reaches: every call there constructs the store with no logger, so
/// <c>logger?.IsEnabled(LogLevel.Debug) == true</c> is always false and the created/pruned log
/// calls never run. These tests supply a real <see cref="ILogger{TCategoryName}"/> whose
/// <c>IsEnabled</c> returns true and capture the formatted message produced by each
/// <c>[LoggerMessage]</c> call, so the assertions verify the log body actually executed rather
/// than only that the null-conditional receiver was evaluated.
/// Uses <see cref="PostgresTestBase"/> (same fixture pattern as
/// <c>DapperPerspectiveStreamLockerCoverageTests</c>) against a real PostgreSQL instance, since
/// both branches only fire after a live INSERT/DELETE against wh_perspective_snapshots actually
/// succeeds.
/// </summary>
public class DapperPerspectiveSnapshotStoreCoverageTests : IDisposable {
  private TestFixture _testBase = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    _testBase = new TestFixture();
    await _testBase.SetupAsync();
  }

  public void Dispose() {
    _testBase?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    GC.SuppressFinalize(this);
  }

  [After(Test)]
  public async Task CleanupAsync() {
    await _testBase.DisposeAsync();
  }

  /// <summary>Captures the fully formatted message of every log call, and reports itself enabled for
  /// every level so the store's `logger?.IsEnabled(LogLevel.Debug) == true` guard passes.</summary>
  private sealed class _capturingLogger : ILogger<DapperPerspectiveSnapshotStore> {
    private readonly List<string> _messages = [];
    public List<string> Messages => _messages;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (_messages) { _messages.Add(formatter(state, exception)); }
    }
  }

  // A snapshot exists so a projection need not replay from zero. If the creation log call
  // regressed, an operator investigating why a rewind took the slow (full-replay) path would lose
  // the one line proving a snapshot was actually written, and at which event.
  [Test]
  public async Task CreateSnapshotAsync_DebugEnabled_LogsCreationWithDetailAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "CoverageSnapshotPerspective";
    var snapshotEventId = Guid.CreateVersion7();
    var logger = new _capturingLogger();
    var store = new DapperPerspectiveSnapshotStore(_testBase.TestConnectionString, logger);

    await store.CreateSnapshotAsync(streamId, perspectiveName, snapshotEventId, JsonDocument.Parse("""{"x":1}"""));

    await Assert.That(logger.Messages).Count().IsEqualTo(1)
      .Because("exactly one debug log call must fire when a snapshot is successfully created");
    var message = logger.Messages[0];
    await Assert.That(message).Contains(perspectiveName)
      .Because("the created-snapshot message must actually run, not just the null-conditional receiver");
    await Assert.That(message).Contains(streamId.ToString());
    await Assert.That(message).Contains(snapshotEventId.ToString());

    // A snapshot a projection cannot read back correctly is worse than no snapshot at all — assert
    // the row genuinely round-trips the payload, proving the log call followed a real INSERT.
    await using var conn = new NpgsqlConnection(_testBase.TestConnectionString);
    await conn.OpenAsync();
    var stored = await conn.QuerySingleAsync<string>(
      "SELECT snapshot_data::text FROM wh_perspective_snapshots WHERE stream_id = @p AND perspective_name = @n",
      new { p = streamId, n = perspectiveName });
    var storedValue = JsonDocument.Parse(stored).RootElement.GetProperty("x").GetInt32();
    await Assert.That(storedValue).IsEqualTo(1);
  }

  // A prune keeps a bounded snapshot history. If the pruned-count log call regressed, an operator
  // could not tell "prune ran and found nothing to remove" from "prune removed a batch" — the
  // exact signal needed when diagnosing unbounded snapshot table growth.
  [Test]
  public async Task PruneOldSnapshotsAsync_DebugEnabledAndRowsDeleted_LogsPrunedCountWithDetailAsync() {
    var streamId = Guid.CreateVersion7();
    const string perspectiveName = "CoveragePrunePerspective";
    var writer = new DapperPerspectiveSnapshotStore(_testBase.TestConnectionString);
    await writer.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(), JsonDocument.Parse("{}"));
    await writer.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(), JsonDocument.Parse("{}"));
    await writer.CreateSnapshotAsync(streamId, perspectiveName, Guid.CreateVersion7(), JsonDocument.Parse("{}"));
    var logger = new _capturingLogger();
    var store = new DapperPerspectiveSnapshotStore(_testBase.TestConnectionString, logger);

    await store.PruneOldSnapshotsAsync(streamId, perspectiveName, keepCount: 1);

    await Assert.That(logger.Messages).Count().IsEqualTo(1)
      .Because("exactly one debug log call must fire when the prune actually deletes rows");
    var message = logger.Messages[0];
    await Assert.That(message).Contains("Pruned 2 old snapshots for " + perspectiveName)
      .Because("the pruned-count message must actually run with the real deleted count, not just the null-conditional receiver");

    // Assert the surviving row count directly — the log claims 2 deleted, but only a real query
    // proves the store actually kept the one it promised (not zero, not all three).
    await using var conn = new NpgsqlConnection(_testBase.TestConnectionString);
    await conn.OpenAsync();
    var remaining = await conn.ExecuteScalarAsync<long>(
      "SELECT COUNT(*) FROM wh_perspective_snapshots WHERE stream_id = @p AND perspective_name = @n",
      new { p = streamId, n = perspectiveName });
    await Assert.That(remaining).IsEqualTo(1L)
      .Because("keepCount=1 must leave exactly the most recent snapshot behind");
  }

  private sealed class TestFixture : PostgresTestBase {
    public string TestConnectionString => ConnectionString;
  }
}
