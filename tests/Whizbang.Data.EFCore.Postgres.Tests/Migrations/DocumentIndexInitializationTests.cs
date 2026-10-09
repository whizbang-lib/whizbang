// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// The shipped schema pass, end to end: it builds the document indexes a model declares, retires one it no
/// longer declares through the managed-object ledger, and does not build a second index over a definition the
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

    await Assert.That(await _indexesAsync(UNDECLARED, "gin (data)")).IsEmpty()
      .Because("the whole-document index is off unless asked for, from 1.0");
    await Assert.That(await _indexesAsync(UNDECLARED, "gin (metadata)")).IsEmpty()
      .Because("the metadata index is off unless asked for");
    await Assert.That(await _indexesAsync(OPTED_OUT, "gin (data)")).IsEmpty();
    await Assert.That(await _indexesAsync(OPTED_OUT, "(data ->> 'Status'::text)")).Count().IsEqualTo(1)
      .Because("the declared field index is how the opted-out model answers its filter");
    await Assert.That(await _indexesAsync(METADATA, "gin (data)")).Count().IsEqualTo(1);
    await Assert.That(await _indexesAsync(METADATA, "gin (metadata)")).Count().IsEqualTo(1);
  }

  /// <summary>
  /// An index an earlier release built, which this release no longer declares, is recorded the first time
  /// the reconcile sees it and dropped on the start after that; one Whizbang did not build is never dropped.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task AnIndexNoLongerDeclared_IsRecordedFirst_ThenDroppedOnTheNextStartAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);
    // What an earlier release left: both document indexes on every table. And one a DBA added.
    await _execAsync($"""
      CREATE INDEX idx_document_index_opted_out_data_gin ON {OPTED_OUT} USING gin (data);
      CREATE INDEX idx_document_index_undeclared_data_gin ON {UNDECLARED} USING gin (data);
      CREATE INDEX idx_document_index_undeclared_metadata_gin ON {UNDECLARED} USING gin (metadata);
      CREATE INDEX reporting_status ON {UNDECLARED} ((data ->> 'Status'));
      """);

    await _initializeAsync(cancellationToken);

    await Assert.That(await _indexesAsync(OPTED_OUT, "gin (data)"))
      .IsEquivalentTo(["idx_document_index_opted_out_data_gin"])
      .Because("the start that first sees an undeclared index records it as pending retirement and drops nothing");

    await _initializeAsync(cancellationToken);

    await Assert.That(await _indexesAsync(OPTED_OUT, "gin (data)")).IsEmpty();
    await Assert.That(await _indexesAsync(UNDECLARED, "gin (data)")).IsEmpty();
    await Assert.That(await _indexesAsync(UNDECLARED, "gin (metadata)")).IsEmpty();
    await Assert.That(await _indexesAsync(UNDECLARED, "reporting_status")).IsEquivalentTo(["reporting_status"])
      .Because("an index Whizbang did not build is foreign, and a foreign object is never dropped");
  }

  /// <summary>
  /// An index the model declares and someone dropped by hand is built again at the next start, though nothing
  /// in the model changed and the schema pass would otherwise skip the table.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task ADeclaredIndexDroppedByHand_IsBuiltAgainAtTheNextStartAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);
    var declared = await _indexesAsync(UNDECLARED, "Status");
    await Assert.That(declared).Count().IsEqualTo(1);
    await _execAsync($"DROP INDEX {declared[0]}");

    await _initializeAsync(cancellationToken);

    await Assert.That(await _indexesAsync(UNDECLARED, "Status")).IsEquivalentTo(declared);
  }

  /// <summary>
  /// An index the model pins with <c>[KeepSchemaObject]</c> survives every start, and the ledger records the pin
  /// as the model's.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task AnObjectTheModelPins_IsKeptAndRecordedAsPinnedByCodeAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);
    await _execAsync($"CREATE INDEX idx_document_index_opted_out_legacy ON {OPTED_OUT} ((data ->> 'Code'))");

    await _initializeAsync(cancellationToken);
    await _initializeAsync(cancellationToken);

    await Assert.That(await _indexesAsync(OPTED_OUT, "Code")).IsEquivalentTo(["idx_document_index_opted_out_legacy"]);
    await using var db = new NpgsqlConnection(_connectionString);
    await db.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand("""
      SELECT code_pinned::text || '|' || code_pin_source || '|' || code_pin_reason FROM wh_managed_objects
      WHERE object_name = 'idx_document_index_opted_out_legacy'
      """, db);
    await Assert.That(await command.ExecuteScalarAsync(cancellationToken)).IsEqualTo("true|code|the reporting job reads it");
  }

  /// <summary>
  /// An index the model declares that exists only as a twin under an earlier name is the declared index: the
  /// reconcile keeps it on every start, and does not report the declared name missing.
  /// </summary>
  [Test]
  [Timeout(180000)]
  public async Task ADeclaredIndexThatExistsUnderAnEarlierName_IsKeptOnEveryStartAsync(CancellationToken cancellationToken) {
    await _initializeAsync(cancellationToken);
    // The state an earlier release left: the declared index under an earlier name in Whizbang's own naming (a
    // long table's name truncated, where the declared one is hashed), and no index under the declared name.
    await _execAsync($"""
      DROP INDEX idx_document_index_undeclared_scope_tenant;
      CREATE INDEX idx_document_index_undeclared_scope_t ON {UNDECLARED} ((scope->>'t'));
      """);
    await _forgetPerspectiveHashesAsync();

    await _initializeAsync(cancellationToken);
    await _initializeAsync(cancellationToken);
    await _initializeAsync(cancellationToken);

    await Assert.That(await _indexesAsync(UNDECLARED, "(scope ->> 't'::text)"))
      .IsEquivalentTo(["idx_document_index_undeclared_scope_t"])
      .Because("the earlier name stands for the declared index, so it is neither dropped nor joined by a twin");
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
