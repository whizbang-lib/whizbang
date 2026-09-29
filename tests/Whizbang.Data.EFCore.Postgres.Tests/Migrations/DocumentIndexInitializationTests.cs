using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The shipped schema pass, end to end: it builds the document indexes a model declares, never
/// removes one it no longer declares, and does not build a second index over a definition the
/// table already has.
/// </summary>
/// <remarks>
/// <para>
/// The generator tests say what is emitted and the function tests say what one statement does.
/// Neither says what an upgrade does to a database an earlier release built, which is where each
/// of these properties matters: an index a production query depends on must survive an upgrade
/// that stopped declaring it, and an index an earlier path created under another name must not be
/// joined by a twin.
/// </para>
/// <para>
/// Each upgrade is simulated by removing the perspective pass's hash rows, which is exactly what a
/// release changing a perspective's DDL looks like to the pass: the hash no longer matches, so the
/// table's script runs again.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-indexes</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class DocumentIndexInitializationTests {
  private const string UNDECLARED = "wh_per_document_index_undeclared";
  private const string OPTED_OUT = "wh_per_document_index_opted_out";
  private const string METADATA = "wh_per_document_index_metadata";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("doc_indexes");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private async Task _initializeAsync(CancellationToken cancellationToken) {
    await using var context = new DocumentIndexesDbContext(
      new DbContextOptionsBuilder<DocumentIndexesDbContext>()
        .UseNpgsql(_connectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
        .Options);
    await context.EnsureWhizbangDatabaseInitializedAsync(cancellationToken: cancellationToken);
  }

  private async Task _execAsync(string sql) {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(sql, db);
    await command.ExecuteNonQueryAsync();
  }

  /// <summary>The indexes on a table whose definition contains a fragment, by name.</summary>
  private async Task<List<string>> _indexesAsync(string table, string fragment = "") {
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync();
    await using var command = new NpgsqlCommand(
      "SELECT indexname FROM pg_indexes WHERE tablename = $1 AND indexdef LIKE '%' || $2 || '%' ORDER BY indexname", db);
    command.Parameters.AddWithValue(table);
    command.Parameters.AddWithValue(fragment);
    var names = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) {
      names.Add(reader.GetString(0));
    }
    return names;
  }

  /// <summary>What a release changing each perspective's DDL looks like to the pass.</summary>
  private Task _forgetPerspectiveHashesAsync() =>
    _execAsync("DELETE FROM wh_schema_migrations WHERE owner = 'perspective'");

  /// <summary>A fresh schema carries the document indexes each model declares, and no others.</summary>
  [Test]
  [Timeout(180000)]
  public async Task AFreshSchemaBuildsOnlyTheDeclaredDocumentIndexesAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);

    await Assert.That(await _indexesAsync(UNDECLARED, "gin (data)")).Count().IsEqualTo(1)
      .Because("undeclared keeps the whole-document index for now");
    await Assert.That(await _indexesAsync(UNDECLARED, "gin (metadata)")).IsEmpty()
      .Because("the metadata index is off unless asked for");
    await Assert.That(await _indexesAsync(OPTED_OUT, "gin (data)")).IsEmpty();
    await Assert.That(await _indexesAsync(OPTED_OUT, "(data ->> 'Status'::text)")).Count().IsEqualTo(1)
      .Because("the declared field index is how the opted-out model answers its filter");
    await Assert.That(await _indexesAsync(METADATA, "gin (data)")).Count().IsEqualTo(1);
    await Assert.That(await _indexesAsync(METADATA, "gin (metadata)")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// An index an earlier release built, which this release no longer declares, is still there
  /// after the pass runs again.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task AnIndexNoLongerDeclaredIsLeftInPlaceAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);
    // What an earlier release left: both document indexes on every table.
    await _execAsync($"""
      CREATE INDEX idx_document_index_opted_out_data_gin ON {OPTED_OUT} USING gin (data);
      CREATE INDEX idx_document_index_undeclared_metadata_gin ON {UNDECLARED} USING gin (metadata);
      """);
    await _forgetPerspectiveHashesAsync();

    await _initializeAsync(cancellationToken);

    await Assert.That(await _indexesAsync(OPTED_OUT, "gin (data)"))
      .IsEquivalentTo(["idx_document_index_opted_out_data_gin"])
      .Because("dropping an index a production query may use is an operator's decision, never an upgrade's");
    await Assert.That(await _indexesAsync(UNDECLARED, "gin (metadata)"))
      .IsEquivalentTo(["idx_document_index_undeclared_metadata_gin"]);
  }

  /// <summary>
  /// Indexes an earlier path created under its own names are not joined by twins with the
  /// schema's names when the pass runs again.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task AnEquivalentIndexUnderAnotherNameIsNotDuplicatedAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);
    // The state the removed apply-time path left behind: its own names, the same definitions.
    await _execAsync($"""
      DROP INDEX idx_document_index_undeclared_scope_tenant;
      DROP INDEX idx_document_index_undeclared_status_json;
      CREATE INDEX idx_{UNDECLARED}_scope_t ON {UNDECLARED} ((scope->>'t'));
      CREATE INDEX idx_{UNDECLARED}_data_status ON {UNDECLARED} ((data->>'Status'));
      """);
    await _forgetPerspectiveHashesAsync();

    await _initializeAsync(cancellationToken);

    await Assert.That(await _indexesAsync(UNDECLARED, "(scope ->> 't'::text)"))
      .IsEquivalentTo([$"idx_{UNDECLARED}_scope_t"]);
    await Assert.That(await _indexesAsync(UNDECLARED, "(data ->> 'Status'::text)"))
      .IsEquivalentTo([$"idx_{UNDECLARED}_data_status"]);
  }
}
