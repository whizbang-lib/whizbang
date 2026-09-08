using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A retry is not a republish. With deterministic emission identity a re-dispatched handler emits the
/// same message ids, and <c>store_outbox_messages</c> skips the rows that already exist. The driver
/// must read what the store reports and count each skipped row, tagged by type, so the fleet can see
/// how much re-emission its completion path is producing; the rows themselves must not be duplicated.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs</code-under-test>
[Category("Shard4")]
public class EFCoreOutboxEmissionDedupTests : EFCoreTestBase {

  private sealed class _captureLogger : Microsoft.Extensions.Logging.ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>> {
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      lock (Entries) { Entries.Add((logLevel, formatter(state, exception))); }
    }
  }

  /// <summary>A logger with Debug disabled: the forensic line must be skipped without formatting cost.</summary>
  private sealed class _quietLogger : Microsoft.Extensions.Logging.ILogger<EFCoreWorkCoordinator<WorkCoordinationDbContext>> {
    public int Calls;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel > Microsoft.Extensions.Logging.LogLevel.Debug;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      if (logLevel <= Microsoft.Extensions.Logging.LogLevel.Debug) {
        Interlocked.Increment(ref Calls);
      }
    }
  }

  /// <summary>Observes the deduplication counter; the passive counter reports running totals per tag set.</summary>
  private sealed class _dedupObserver : IDisposable {
    private readonly MeterListener _listener = new();
    public List<(long Value, string? MessageType)> Cells { get; } = [];

    public _dedupObserver(WorkCoordinatorMetrics metrics) {
      _listener.InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument.Meter, metrics.ProcessBatchCalls.Meter)
            && instrument.Name == "whizbang.work_coordinator.outbox.emission_deduplicated") {
          l.EnableMeasurementEvents(instrument);
        }
      };
      _listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
        string? messageType = null;
        foreach (var tag in tags) {
          if (tag.Key == "message_type") {
            messageType = tag.Value as string;
          }
        }
        lock (Cells) { Cells.Add((value, messageType)); }
      });
      _listener.Start();
    }

    public List<(long Value, string? MessageType)> Snapshot() {
      lock (Cells) { Cells.Clear(); }
      _listener.RecordObservableInstruments();
      lock (Cells) { return [.. Cells]; }
    }

    public void Dispose() => _listener.Dispose();
  }

  private static async Task<long> _outboxRowCountAsync(WorkCoordinationDbContext dbContext, Guid messageId) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT count(*) FROM wh_outbox WHERE message_id = @msg";
    cmd.Parameters.AddWithValue("msg", messageId);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_SameMessageStoredTwice_CountsTheSkippedRowByTypeAsync() {
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics());
    using var observer = new _dedupObserver(metrics);
    var logger = new _captureLogger();

    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions(), logger, metrics);

    var messageId = (Guid)TrackedGuid.NewMedo();
    var message = CreateTestOutboxMessage(messageId, "out-topic", (Guid)TrackedGuid.NewMedo());

    // First emission: stored, nothing skipped.
    await coordinator.StoreOutboxMessagesAsync([message], partitionCount: 10000);
    await Assert.That(observer.Snapshot().Sum(c => c.Value)).IsEqualTo(0L)
      .Because("a first emission is new; counting it would make every store look like a retry");

    // The retry re-emits the identical message.
    await coordinator.StoreOutboxMessagesAsync([message], partitionCount: 10000);

    var cells = observer.Snapshot();
    await Assert.That(cells.Sum(c => c.Value)).IsEqualTo(1L)
      .Because("the store reported was_newly_created = false for the row; that is the republish the retry would have produced");
    await Assert.That(cells.Single(c => c.Value > 0).MessageType).IsEqualTo(message.MessageType)
      .Because("operators triage by type: which handler is being re-dispatched after its emissions committed");

    await Assert.That(await _outboxRowCountAsync(dbContext, messageId)).IsEqualTo(1L)
      .Because("the primary key absorbs the copy; there is exactly one row to publish");

    List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> entries;
    lock (logger.Entries) { entries = [.. logger.Entries]; }
    var skipped = entries.Where(e => e.Message.Contains("skipped", StringComparison.Ordinal) && e.Message.Contains(messageId.ToString(), StringComparison.Ordinal)).ToList();
    await Assert.That(skipped).Count().IsEqualTo(1)
      .Because("the metric is the alarm; the log names the row for forensics, once");
    await Assert.That(skipped[0].Level).IsEqualTo(Microsoft.Extensions.Logging.LogLevel.Debug);
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_MixedBatch_CountsOnlyTheRowsThatAlreadyExistedAsync() {
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics());
    using var observer = new _dedupObserver(metrics);
    var quiet = new _quietLogger();

    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions(), quiet, metrics);

    var streamId = (Guid)TrackedGuid.NewMedo();
    var existing = CreateTestOutboxMessage((Guid)TrackedGuid.NewMedo(), "out-topic", streamId);
    var fresh = CreateTestOutboxMessage((Guid)TrackedGuid.NewMedo(), "out-topic", streamId);

    await coordinator.StoreOutboxMessagesAsync([existing], partitionCount: 10000);
    await coordinator.StoreOutboxMessagesAsync([existing, fresh], partitionCount: 10000);

    await Assert.That(observer.Snapshot().Sum(c => c.Value)).IsEqualTo(1L)
      .Because("a retry that emits one more event than its first run must have the new one stored and only the repeat counted");
    await Assert.That(await _outboxRowCountAsync(dbContext, existing.MessageId)).IsEqualTo(1L);
    await Assert.That(await _outboxRowCountAsync(dbContext, fresh.MessageId)).IsEqualTo(1L);
    await Assert.That(quiet.Calls).IsEqualTo(0)
      .Because("with Debug disabled the forensic line is skipped entirely; the metric already carries the count");
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_WithoutMetricsOrLogger_StillDeduplicatesQuietlyAsync() {
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions());

    var message = CreateTestOutboxMessage((Guid)TrackedGuid.NewMedo(), "out-topic", (Guid)TrackedGuid.NewMedo());

    await coordinator.StoreOutboxMessagesAsync([message], partitionCount: 10000);
    await coordinator.StoreOutboxMessagesAsync([message], partitionCount: 10000);

    await Assert.That(await _outboxRowCountAsync(dbContext, message.MessageId)).IsEqualTo(1L)
      .Because("observability is optional; the dedup itself is the store's primary key and must hold without it");
  }

  [Test]
  public async Task StoreOutboxMessagesAsync_InsideAnOpenTransaction_StoresAndCountsOnTheSameConnectionAsync() {
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics());
    using var observer = new _dedupObserver(metrics);

    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      dbContext, JsonContextRegistry.CreateCombinedOptions(), logger: null, metrics);

    var message = CreateTestOutboxMessage((Guid)TrackedGuid.NewMedo(), "out-topic", (Guid)TrackedGuid.NewMedo());
    await coordinator.StoreOutboxMessagesAsync([message], partitionCount: 10000);

    // Wrapped in the context's execution strategy so a retrying strategy accepts the user transaction.
    await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
      await using var tx = await dbContext.Database.BeginTransactionAsync();
      await coordinator.StoreOutboxMessagesAsync([message], partitionCount: 10000);
      await tx.CommitAsync();
    });

    await Assert.That(observer.Snapshot().Sum(c => c.Value)).IsEqualTo(1L)
      .Because("the counting read must enlist in the caller's transaction rather than open a second one on the same connection");
    await Assert.That(await _outboxRowCountAsync(dbContext, message.MessageId)).IsEqualTo(1L);
  }
}
