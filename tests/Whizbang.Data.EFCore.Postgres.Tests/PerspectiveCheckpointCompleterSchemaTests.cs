using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Configuration;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Where the rebuild checkpoint completer writes its cursor rows when the consumer's tables live
/// in a service schema rather than <c>public</c>.
/// </summary>
/// <remarks>
/// <para>
/// The completer builds its upsert against a table name it qualifies from the DbContext's model.
/// The multi-schema deployment shape is the one that punishes getting this wrong, and it does so
/// silently: the framework migrations also created <c>public.wh_perspective_cursors</c>, so a bare
/// table name does not fail — it resolves through <c>search_path</c> and writes into the wrong
/// table. The service's own cursors then never advance, so every rebuilt perspective looks
/// permanently un-rebuilt and replays from the beginning on every pass, while <c>public</c>
/// accumulates cursor rows for streams no one there owns.
/// </para>
/// <para>
/// Asserting the row's ABSENCE from <c>public</c> is what makes this a real test: writing to the
/// service schema is not enough to prove qualification if the assertion would also pass on a row
/// that landed in both.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveCheckpointCompleter.cs</code-under-test>
[Category("Integration")]
[Category("Shard1")]
public class PerspectiveCheckpointCompleterSchemaTests : EFCoreTestBase {

  private const string ServiceSchema = "cursor_svc";

  /// <summary>A consumer whose Whizbang tables live in a service schema.</summary>
  private sealed class SchemaScopedDbContext(DbContextOptions<SchemaScopedDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.ConfigureWhizbangInfrastructure();
      modelBuilder.HasDefaultSchema(ServiceSchema);
    }
  }

  [Test]
  public async Task CompleteAsync_WithTablesInAServiceSchema_WritesTheCursorThereAndNotToPublicAsync() {
    var streamId = (Guid)TrackedGuid.NewMedo();
    var lastEventId = (Guid)TrackedGuid.NewMedo();
    const string perspectiveName = "Rebuild.SchemaScoped";

    await using (var setup = CreateDbContext()) {
      var conn = (NpgsqlConnection)setup.Database.GetDbConnection();
      if (conn.State != System.Data.ConnectionState.Open) {
        await conn.OpenAsync();
      }
      await using var ddl = conn.CreateCommand();
      // LIKE ... INCLUDING ALL copies the columns, defaults and the (stream_id, perspective_name)
      // key the upsert conflicts on. Foreign keys are deliberately not copied by LIKE, so the
      // cursor needs no event row behind it — the schema qualification is what is under test.
      ddl.CommandText = $"""
        CREATE SCHEMA IF NOT EXISTS {ServiceSchema};
        CREATE TABLE IF NOT EXISTS {ServiceSchema}.wh_perspective_cursors
          (LIKE public.wh_perspective_cursors INCLUDING ALL);
        """;
      await ddl.ExecuteNonQueryAsync();
    }

    var options = new DbContextOptionsBuilder<SchemaScopedDbContext>()
      .UseNpgsql(ConnectionString)
      .Options;
    await using var schemaScoped = new SchemaScopedDbContext(options);
    var completer = new EFCorePostgresPerspectiveCheckpointCompleter(schemaScoped);

    await completer.CompleteAsync([
      new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = lastEventId,
        Status = PerspectiveProcessingStatus.Completed,
      }
    ]);

    await using var verify = CreateDbContext();
    var verifyConn = (NpgsqlConnection)verify.Database.GetDbConnection();
    if (verifyConn.State != System.Data.ConnectionState.Open) {
      await verifyConn.OpenAsync();
    }

    await Assert.That(await _cursorCountAsync(verifyConn, ServiceSchema, streamId, perspectiveName))
      .IsEqualTo(1L)
      .Because("the model says where wh_perspective_cursors lives, and the rebuild's checkpoint "
             + "has to land there or the service's perspectives never record that they were built");
    await Assert.That(await _cursorCountAsync(verifyConn, "public", streamId, perspectiveName))
      .IsEqualTo(0L)
      .Because("an unqualified table name resolves through search_path and writes into public's "
             + "table instead — which does not fail, it just checkpoints the wrong service");
  }

  private static async Task<long> _cursorCountAsync(
      NpgsqlConnection conn, string schema, Guid streamId, string perspectiveName) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText =
      $"SELECT count(*) FROM {schema}.wh_perspective_cursors "
      + "WHERE stream_id = @stream AND perspective_name = @name";
    cmd.Parameters.AddWithValue("stream", streamId);
    cmd.Parameters.AddWithValue("name", perspectiveName);
    return (long)(await cmd.ExecuteScalarAsync())!;
  }
}
