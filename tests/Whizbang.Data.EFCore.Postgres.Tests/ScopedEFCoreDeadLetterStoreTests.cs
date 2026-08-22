using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the singleton-safe adapter that lets the dispatch /
/// perspective workers inject <see cref="IDeadLetterStore"/> while the
/// underlying EFCore impl still gets a per-call scoped DbContext.
/// </summary>
[Category("Shard3")]
public class ScopedEFCoreDeadLetterStoreTests : EFCoreTestBase {

  // ===== Constructor null guards =====

  [Test]
  public async Task Constructor_NullScopeFactory_ThrowsArgumentNullExceptionAsync() {
    await Assert.That(() => new ScopedEFCoreDeadLetterStore(
      scopeFactory: null!,
      dbContextType: typeof(WorkCoordinationDbContext),
      logger: NullLogger<EFCoreDeadLetterStore<DbContext>>.Instance,
      gate: null))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullDbContextType_ThrowsArgumentNullExceptionAsync() {
    var services = new ServiceCollection();
    await using var sp = services.BuildServiceProvider();
    await Assert.That(() => new ScopedEFCoreDeadLetterStore(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      dbContextType: null!,
      logger: NullLogger<EFCoreDeadLetterStore<DbContext>>.Instance,
      gate: null))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_NullLogger_ThrowsArgumentNullExceptionAsync() {
    var services = new ServiceCollection();
    await using var sp = services.BuildServiceProvider();
    await Assert.That(() => new ScopedEFCoreDeadLetterStore(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      dbContextType: typeof(WorkCoordinationDbContext),
      logger: null!,
      gate: null))
      .Throws<ArgumentNullException>();
  }

  // ===== MoveAsync round-trip via the adapter =====

  [Test]
  public async Task MoveAsync_OutboxRow_AdapterOpensScopeAndDelegatesToInnerStoreAsync() {
    // Wires up the same DI shape the EFCore turnkey path produces: a singleton
    // adapter that resolves the consumer's DbContext from a fresh scope per
    // MoveAsync. End-to-end proof that the row moves into wh_dead_letters.
    var services = new ServiceCollection();
    services.AddDbContext<WorkCoordinationDbContext>(opts => opts.UseNpgsql(ConnectionString));
    await using var sp = services.BuildServiceProvider();

    var adapter = new ScopedEFCoreDeadLetterStore(
      scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
      dbContextType: typeof(WorkCoordinationDbContext),
      logger: NullLogger<EFCoreDeadLetterStore<DbContext>>.Instance,
      gate: null);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    var messageId = (Guid)TrackedGuid.NewMedo();
    await _insertOutboxRowAsync(conn, messageId);

    var dlqId = (Guid)TrackedGuid.NewMedo();
    var result = await adapter.MoveAsync(
      deadLetterId: dlqId,
      sourceTable: DeadLetterSourceTable.OUTBOX,
      sourceId: messageId,
      failureReason: MessageFailureReason.MaxAttemptsExceeded,
      errorText: "via singleton adapter",
      instanceId: (Guid)TrackedGuid.NewMedo(),
      generation: "v0.505-scoped");

    await Assert.That(result).IsEqualTo(dlqId)
      .Because("adapter returns the inner store's result unchanged");

    var outboxCount = await _countAsync(conn, "wh_outbox", "message_id", messageId);
    await Assert.That(outboxCount).IsEqualTo(0).Because("source row removed by SQL fn");
    var dlqCount = await _countAsync(conn, "wh_dead_letters", "dead_letter_id", dlqId);
    await Assert.That(dlqCount).IsEqualTo(1).Because("DLQ snapshot inserted by SQL fn");
  }

  // ===== Helpers (mirror EFCoreDeadLetterStoreTests) =====

  private static async Task _insertOutboxRowAsync(NpgsqlConnection conn, Guid messageId) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      INSERT INTO wh_outbox
        (message_id, destination, message_type, envelope_type, event_data, metadata, status, attempts,
         created_at, stream_id, partition_number)
      VALUES (@msg, 'topic', 'TestEvent', 'TestEnvelope', '{}', '{}', 1, 11, NOW(), @stream, 0)";
    cmd.Parameters.AddWithValue("msg", messageId);
    cmd.Parameters.AddWithValue("stream", (Guid)TrackedGuid.NewMedo());
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int> _countAsync(NpgsqlConnection conn, string table, string column, Guid id) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = @id";
    cmd.Parameters.AddWithValue("id", id);
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
  }
}
