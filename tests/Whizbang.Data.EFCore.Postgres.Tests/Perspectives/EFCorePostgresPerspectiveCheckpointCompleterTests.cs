using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Integration tests for <see cref="EFCorePostgresPerspectiveCheckpointCompleter"/>, the
/// EF Core half of cursor completion. Rebuild persists checkpoints through this path, so a
/// silent failure here leaves wh_perspective_cursors at whatever live processing last wrote
/// while the projection tables move on.
/// </summary>
[Category("Integration")]
[Category("Shard2")]
public class EFCorePostgresPerspectiveCheckpointCompleterTests : EFCoreTestBase {

  private const string PERSPECTIVE_NAME = "Whizbang.Tests.CheckpointCompleterPerspective";

  /// <summary>Captures emitted entries so the log-branch tests can assert they fired.</summary>
  private sealed class RecordingLogger<T>(bool debugEnabled)
      : ILogger<EFCorePostgresPerspectiveCheckpointCompleter> {
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.Debug || debugEnabled;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
      => Entries.Add((logLevel, formatter(state, exception), exception));

    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  /// <summary>
  /// Seeds a wh_event_store row so a cursor's last_event_id points at a real event.
  /// wh_perspective_cursors carries fk_perspective_cursors_event, so a checkpoint can only
  /// name an event that exists — an invariant worth honouring rather than working around.
  /// </summary>
  private async Task<(Guid StreamId, Guid EventId)> _seedStreamWithEventAsync(int version = 1) {
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();

    await using var conn = new Npgsql.NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await using var cmd = new Npgsql.NpgsqlCommand(@"
      INSERT INTO wh_event_store
        (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
      VALUES (@id, @stream, @stream, 'TestAgg', @version, 'Test.Checkpointed, Test',
              '{}'::jsonb, NOW())", conn);
    cmd.Parameters.AddWithValue("id", eventId);
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("version", version);
    await cmd.ExecuteNonQueryAsync();

    return (streamId, eventId);
  }

  private static PerspectiveCursorCompletion _completion(Guid streamId, Guid lastEventId)
    => new() {
      StreamId = streamId,
      PerspectiveName = PERSPECTIVE_NAME,
      LastEventId = lastEventId,
      Status = PerspectiveProcessingStatus.Completed,
    };

  [Test]
  public async Task CompleteAsync_WithNullCompletions_ThrowsAsync() {
    await using var ctx = CreateDbContext();
    var completer = new EFCorePostgresPerspectiveCheckpointCompleter(ctx);

    await Assert.That(async () => await completer.CompleteAsync(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task CompleteAsync_WithNoCompletions_DoesNoWorkAsync() {
    // The early return matters: opening a transaction per empty rebuild batch would be
    // pure overhead on streams with nothing to checkpoint.
    await using var ctx = CreateDbContext();
    var logger = new RecordingLogger<EFCorePostgresPerspectiveCheckpointCompleter>(debugEnabled: true);
    var completer = new EFCorePostgresPerspectiveCheckpointCompleter(ctx, logger);

    await completer.CompleteAsync([]);

    await Assert.That(logger.Entries).IsEmpty();
  }

  [Test]
  public async Task Constructor_WithNullDbContext_ThrowsAsync() {
    await Assert.That(() => new EFCorePostgresPerspectiveCheckpointCompleter(null!))
        .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task CompleteAsync_PersistsTheCursorAsync() {
    await using var ctx = CreateDbContext();
    var completer = new EFCorePostgresPerspectiveCheckpointCompleter(ctx);
    var (streamId, eventId) = await _seedStreamWithEventAsync();

    await completer.CompleteAsync([_completion(streamId, eventId)]);

    await using var verify = CreateDbContext();
    var stored = await verify.Database
        .SqlQueryRaw<Guid>(
          "SELECT last_event_id AS \"Value\" FROM wh_perspective_cursors "
          + "WHERE stream_id = {0} AND perspective_name = {1}", streamId, PERSPECTIVE_NAME)
        .ToListAsync();

    await Assert.That(stored).Contains(eventId);
  }

  [Test]
  public async Task CompleteAsync_SecondCompletionForTheSameStream_AdvancesTheCursorAsync() {
    // The upsert is ON CONFLICT DO UPDATE: a rebuild re-checkpointing a stream must move
    // the cursor forward rather than fail on the primary key.
    await using var ctx = CreateDbContext();
    var completer = new EFCorePostgresPerspectiveCheckpointCompleter(ctx);
    var (streamId, first) = await _seedStreamWithEventAsync(version: 1);
    var second = Guid.CreateVersion7();

    await using (var conn = new Npgsql.NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync();
      await using var cmd = new Npgsql.NpgsqlCommand(@"
        INSERT INTO wh_event_store
          (event_id, stream_id, aggregate_id, aggregate_type, version, event_type, scope, created_at)
        VALUES (@id, @stream, @stream, 'TestAgg', 2, 'Test.Checkpointed, Test',
                '{}'::jsonb, NOW())", conn);
      cmd.Parameters.AddWithValue("id", second);
      cmd.Parameters.AddWithValue("stream", streamId);
      await cmd.ExecuteNonQueryAsync();
    }

    await completer.CompleteAsync([_completion(streamId, first)]);
    await completer.CompleteAsync([_completion(streamId, second)]);

    await using var verify = CreateDbContext();
    var stored = await verify.Database
        .SqlQueryRaw<Guid>(
          "SELECT last_event_id AS \"Value\" FROM wh_perspective_cursors "
          + "WHERE stream_id = {0} AND perspective_name = {1}", streamId, PERSPECTIVE_NAME)
        .ToListAsync();

    await Assert.That(stored).HasSingleItem();
    await Assert.That(stored[0]).IsEqualTo(second);
  }

  [Test]
  public async Task CompleteAsync_WithAnEmptyLastEventId_SkipsThatCompletionAsync() {
    // Guid.Empty means the perspective processed nothing for that stream. Writing it
    // would move the cursor to a value no event carries, stranding the stream.
    await using var ctx = CreateDbContext();
    var logger = new RecordingLogger<EFCorePostgresPerspectiveCheckpointCompleter>(debugEnabled: true);
    var completer = new EFCorePostgresPerspectiveCheckpointCompleter(ctx, logger);
    var streamId = Guid.CreateVersion7();

    await completer.CompleteAsync([_completion(streamId, Guid.Empty)]);

    await using var verify = CreateDbContext();
    var stored = await verify.Database
        .SqlQueryRaw<Guid>(
          "SELECT last_event_id AS \"Value\" FROM wh_perspective_cursors "
          + "WHERE stream_id = {0} AND perspective_name = {1}", streamId, PERSPECTIVE_NAME)
        .ToListAsync();

    await Assert.That(stored).IsEmpty();
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Debug)).IsTrue();
  }

  [Test]
  public async Task CompleteAsync_MixedBatch_PersistsTheRealOnesAndCountsTheSkipsAsync() {
    await using var ctx = CreateDbContext();
    var logger = new RecordingLogger<EFCorePostgresPerspectiveCheckpointCompleter>(debugEnabled: false);
    var completer = new EFCorePostgresPerspectiveCheckpointCompleter(ctx, logger);
    var (persisted, eventId) = await _seedStreamWithEventAsync();
    var skipped = Guid.CreateVersion7();

    await completer.CompleteAsync([
      _completion(persisted, eventId),
      _completion(skipped, Guid.Empty),
    ]);

    await using var verify = CreateDbContext();
    var stored = await verify.Database
        .SqlQueryRaw<Guid>(
          "SELECT stream_id AS \"Value\" FROM wh_perspective_cursors WHERE perspective_name = {0}",
          PERSPECTIVE_NAME)
        .ToListAsync();

    await Assert.That(stored).Contains(persisted);
    await Assert.That(stored).DoesNotContain(skipped);

    // The Information summary reports both halves so an operator can see skips without Debug on.
    await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Information)).IsTrue();
  }

  [Test]
  public async Task CompleteAsync_JoinsAnAmbientTransactionRatherThanOpeningItsOwnAsync() {
    // Rebuild runs inside a transaction; the completer must enlist so the checkpoint and
    // the projection rows commit together, and must not commit the caller's transaction.
    await using var ctx = CreateDbContext();
    var completer = new EFCorePostgresPerspectiveCheckpointCompleter(ctx);
    var (streamId, eventId) = await _seedStreamWithEventAsync();

    await using var tx = await ctx.Database.BeginTransactionAsync();
    await completer.CompleteAsync([_completion(streamId, eventId)]);
    await tx.RollbackAsync();

    await using var verify = CreateDbContext();
    var stored = await verify.Database
        .SqlQueryRaw<Guid>(
          "SELECT last_event_id AS \"Value\" FROM wh_perspective_cursors "
          + "WHERE stream_id = {0} AND perspective_name = {1}", streamId, PERSPECTIVE_NAME)
        .ToListAsync();

    await Assert.That(stored).IsEmpty()
      .Because("the completer enlisted in the caller's transaction, so rolling it back "
             + "discards the checkpoint too");
  }

  [Test]
  public async Task CompleteAsync_WithDebugEnabled_LogsEachUpsertAsync() {
    await using var ctx = CreateDbContext();
    var logger = new RecordingLogger<EFCorePostgresPerspectiveCheckpointCompleter>(debugEnabled: true);
    var completer = new EFCorePostgresPerspectiveCheckpointCompleter(ctx, logger);
    var (streamId, eventId) = await _seedStreamWithEventAsync();

    await completer.CompleteAsync([_completion(streamId, eventId)]);

    await Assert.That(logger.Entries.Any(e =>
      e.Level == LogLevel.Debug && e.Message.Contains("upserted cursor", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task CompleteAsync_WhenTheUpsertFails_RollsBackAndRethrowsAsync() {
    // A checkpoint naming an event that does not exist violates fk_perspective_cursors_event.
    // The completer owns the transaction here, so it must roll back rather than leave a
    // partial batch committed, log the failure with the count it got through, and rethrow —
    // swallowing it would let rebuild believe the stream was checkpointed.
    await using var ctx = CreateDbContext();
    var logger = new RecordingLogger<EFCorePostgresPerspectiveCheckpointCompleter>(debugEnabled: false);
    var completer = new EFCorePostgresPerspectiveCheckpointCompleter(ctx, logger);
    var (goodStream, goodEvent) = await _seedStreamWithEventAsync();
    var orphanStream = Guid.CreateVersion7();
    var orphanEvent = Guid.CreateVersion7();

    await Assert.That(async () => await completer.CompleteAsync([
      _completion(goodStream, goodEvent),
      _completion(orphanStream, orphanEvent),
    ])).Throws<Exception>();

    await using var verify = CreateDbContext();
    var stored = await verify.Database
        .SqlQueryRaw<Guid>(
          "SELECT stream_id AS \"Value\" FROM wh_perspective_cursors WHERE perspective_name = {0}",
          PERSPECTIVE_NAME)
        .ToListAsync();

    await Assert.That(stored).IsEmpty()
      .Because("the completer opened the transaction, so the row written before the failure "
             + "is rolled back with it rather than left behind");
    await Assert.That(logger.Entries.Any(e => e.Exception is not null)).IsTrue();
  }
}
