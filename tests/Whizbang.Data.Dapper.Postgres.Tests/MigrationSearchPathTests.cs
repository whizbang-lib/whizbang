// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// A migration of a named schema runs with <c>current_schema()</c> resolving to that schema, and only
/// for as long as the migration runs.
/// </summary>
/// <remarks>
/// Shipped migrations are never edited, and several of their guards compare
/// <c>table_schema = current_schema()</c>. On a connection with the default <c>search_path</c> that is
/// <c>public</c>, so a guard re-run on upgrade over a named schema could not see what it had created.
/// </remarks>
/// <docs>contributors/data-engines/writing-migrations</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationSearchPath.cs</code-under-test>
[Category("Integration")]
public class MigrationSearchPathTests {
  private const string SCHEMA = "svc_path";

  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("search_path");
  }

  [After(Test)]
  public async Task TeardownAsync() => await PerTestDatabaseFactory.DropAsync(_database.Name);

  [Test]
  public async Task ANamedSchema_IsSearchedFirst_ThenPublicAsync() {
    await Assert.That(MigrationSearchPath.For("svc")).IsEqualTo("\"svc\", public");
    await Assert.That(MigrationSearchPath.For("\"svc\"")).IsEqualTo("\"svc\", public")
      .Because("the EF Core driver hands the schema over quoted, the Dapper driver bare");
  }

  [Test]
  public async Task Public_OrNoSchema_NeedsNoPathAsync() {
    await Assert.That(MigrationSearchPath.For("public")).IsNull();
    await Assert.That(MigrationSearchPath.For("\"public\"")).IsNull();
    await Assert.That(MigrationSearchPath.For(string.Empty)).IsNull();
    await Assert.That(MigrationSearchPath.For(null)).IsNull()
      .Because("the default path already resolves current_schema() to public");
  }

  [Test]
  public async Task InsideTheMigration_CurrentSchemaIsTheMigratedSchema_AndAfterItIsNotAsync() {
    await using var connection = await _openWithSchemaAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    var previous = await MigrationSearchPath.EnterAsync(connection, SCHEMA);
    var during = await _currentSchemaAsync(connection);
    await MigrationSearchPath.ExitAsync(connection, previous);
    var after = await _currentSchemaAsync(connection);

    await Assert.That(during).IsEqualTo(SCHEMA)
      .Because("a shipped guard asking current_schema() must be asking about the schema it migrates");
    await Assert.That(after).IsEqualTo("public")
      .Because("the next statement in the same transaction resolves names as it did before");
    await Assert.That(previous).IsEqualTo("\"$user\", public");
  }

  [Test]
  public async Task TheSettingEndsWithTheTransactionAsync() {
    await using var connection = await _openWithSchemaAsync();
    await using (var transaction = await connection.BeginTransactionAsync()) {
      await MigrationSearchPath.EnterAsync(connection, SCHEMA);
      await transaction.RollbackAsync();
    }

    await Assert.That(await _currentSchemaAsync(connection)).IsEqualTo("public")
      .Because("a migration that fails aborts the transaction, and the path goes with it");
  }

  [Test]
  public async Task ThePublicSchema_ChangesNothingAsync() {
    await using var connection = await _openWithSchemaAsync();
    await using var transaction = await connection.BeginTransactionAsync();

    var previous = await MigrationSearchPath.EnterAsync(connection, "public");
    await MigrationSearchPath.ExitAsync(connection, previous);

    await Assert.That(previous).IsNull();
    await Assert.That(await _currentSchemaAsync(connection)).IsEqualTo("public");
  }

  [Test]
  public async Task AMissingConnection_IsRefusedAsync() {
    await Assert.That(() => MigrationSearchPath.EnterAsync(null!, SCHEMA)).Throws<ArgumentNullException>();
    await Assert.That(() => MigrationSearchPath.ExitAsync(null!, "public")).Throws<ArgumentNullException>();
  }

  private async Task<NpgsqlConnection> _openWithSchemaAsync() {
    var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var create = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {SCHEMA}", connection);
    await create.ExecuteNonQueryAsync();
    return connection;
  }

  private static async Task<string?> _currentSchemaAsync(NpgsqlConnection connection) {
    await using var command = new NpgsqlCommand("SELECT current_schema()", connection);
    return (string?)await command.ExecuteScalarAsync();
  }
}
