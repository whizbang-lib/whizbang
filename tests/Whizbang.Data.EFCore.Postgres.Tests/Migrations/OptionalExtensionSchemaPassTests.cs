using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The perspective pass applies an optional-extension block through the initializer's own
/// transaction, and the indexes inside the block are built.
/// </summary>
/// <remarks>
/// <para>
/// A script carrying a block does not go to <c>ExecuteSqlRawAsync</c>; it goes to
/// <c>OptionalExtensionBlocks.ApplyAsync</c>, piece by piece, on the context's own connection
/// inside the transaction the initializer has open. That is a different execution path from every
/// other schema script, and the block reader's own tests drive it on a connection they opened
/// themselves. This drives it where it actually runs, inside
/// <c>EnsureWhizbangDatabaseInitializedAsync</c> against a real database.
/// </para>
/// <para>
/// What it pins is that a server which allows the extension still gets the indexes: the skip is a
/// concession to a server that refuses, and a skip that fired anyway, or a block the pass never
/// applied, would leave a declared index silently absent while every start looked healthy. Making
/// every extension read as refused turns this red and leaves the assertions outside the block
/// green, which is the shape of that failure.
/// </para>
/// <para>
/// The refusal itself cannot be reached through a generated script, because a generated script
/// names a real extension that the server either allows or does not, so the refusal is driven
/// through the reader in <c>OptionalExtensionBlocksTests</c> with an extension no server has.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class OptionalExtensionSchemaPassTests {
  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = $"optextpass_{Guid.NewGuid():N}";
    await using (var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString)) {
      await admin.OpenAsync();
      await using var create = new NpgsqlCommand($"CREATE DATABASE {_databaseName}", admin);
      await create.ExecuteNonQueryAsync();
    }
    _connectionString = new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
      Timezone = "UTC",
    }.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is null) {
      return;
    }
    try {
      await using var admin = new NpgsqlConnection(SharedPostgresContainer.ConnectionString);
      await admin.OpenAsync();
      await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)", admin);
      await drop.ExecuteNonQueryAsync();
    } catch (NpgsqlException) {
      // The container goes with the run; a database left behind costs nothing.
    }
  }

  private async Task<string> _scalarAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    return (await command.ExecuteScalarAsync())?.ToString() ?? "<null>";
  }

  /// <summary>
  /// Initialization completes, the extension is installed, and the index that needed it is built.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task TheBlockIsAppliedInsideTheInitializersTransactionAsync(CancellationToken cancellationToken) {
    await using (var context = new CollidingNamesDbContext(
        new DbContextOptionsBuilder<CollidingNamesDbContext>()
          .UseNpgsql(_connectionString)
          .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
          .Options)) {
      await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
    }

    await Assert.That(await _scalarAsync("SELECT count(*) FROM pg_extension WHERE extname = 'pg_trgm'")).IsEqualTo("1")
      .Because("the block's one CREATE EXTENSION runs under a savepoint in the initializer's transaction, "
        + "and that transaction commits with the rest of the pass");
    await Assert.That(await _scalarAsync(
        "SELECT count(*) FROM pg_indexes WHERE tablename = 'wh_per_colliding_first' "
        + "AND indexdef LIKE '%gin_trgm_ops%'")).IsEqualTo("1")
      .Because("an index inside an applied block is an index, not a statement the pass skipped");
    await Assert.That(await _scalarAsync(
        "SELECT count(*) FROM pg_indexes WHERE tablename = 'wh_per_colliding_first' "
        + "AND indexname = 'idx_colliding_first_created_at'")).IsEqualTo("1")
      .Because("the statements outside the block are applied on the same path and in the same pass");
  }
}
