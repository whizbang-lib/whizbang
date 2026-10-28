// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// What the server does with catalog-checked DDL (#1636): with the column or index already there it takes no lock,
/// so it completes while a worker holds the table; with it missing it does exactly what the statement did; and a
/// missing table still fails the way the statement failed.
/// </summary>
/// <remarks>
/// <para>
/// The worker is a real session in an open transaction that has read and written the table, which is what a vote, a
/// heartbeat or a claim holds. The statement under test runs with a <c>lock_timeout</c>, so a statement that waits for
/// the worker fails with <c>55P03</c> rather than hanging: the timeout is how a wait is detected, never how one is
/// ended, since the worker holds its locks until the test is over.
/// </para>
/// <para>
/// The first test is the characterization: the same statement unguarded waits behind the worker. Without it the
/// others could pass against a server that never locked in the first place.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/CatalogCheckedDdl.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard5")]
public sealed class CatalogCheckedDdlPostgresTests {
  private PerTestDatabase _database;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _database = await PerTestDatabaseFactory.CreateAsync("ddlguard");
    await _executeAsync("""
      CREATE SCHEMA "Odd Schema";
      CREATE TABLE public.held (id int, present int);
      CREATE INDEX held_present ON public.held (present);
      CREATE TABLE "Odd Schema"."Held" (id int, "Present" int);
      """);
  }

  [After(Test)]
  public async Task TeardownAsync() {
    NpgsqlConnection.ClearAllPools();
    await PerTestDatabaseFactory.DropAsync(_database.Name);
  }

  [Test]
  [Timeout(60000)]
  public async Task Unguarded_AnAddColumnOfAColumnThatExists_StillWaitsForTheWorkerAsync(CancellationToken cancellationToken) {
    await using var worker = await _workerHoldingAsync("public.held", cancellationToken);

    var failure = await Assert.That(async () => await _runWithLockTimeoutAsync(
        "ALTER TABLE public.held ADD COLUMN IF NOT EXISTS present int;", cancellationToken))
      .Throws<PostgresException>();

    await Assert.That(failure!.SqlState).IsEqualTo("55P03")
      .Because("ADD COLUMN IF NOT EXISTS takes ACCESS EXCLUSIVE before it finds the column, which is the defect");
  }

  [Test]
  [Timeout(60000)]
  public async Task Guarded_AnAddColumnOfAColumnThatExists_TakesNoLock_SoTheWorkerDoesNotHoldItUpAsync(
      CancellationToken cancellationToken) {
    await using var worker = await _workerHoldingAsync("public.held", cancellationToken);

    await _runWithLockTimeoutAsync(
      CatalogCheckedDdl.Guard("ALTER TABLE public.held ADD COLUMN IF NOT EXISTS present int;"), cancellationToken);

    await Assert.That(await _workerStateAsync(worker, cancellationToken)).IsEqualTo("idle in transaction")
      .Because("the statement completed while the worker still held the table");
  }

  [Test]
  [Timeout(60000)]
  public async Task Guarded_AnIndexThatExists_TakesNoLockAsync(CancellationToken cancellationToken) {
    await using var worker = await _workerHoldingAsync("public.held", cancellationToken);

    await _runWithLockTimeoutAsync(
      CatalogCheckedDdl.Guard("CREATE INDEX IF NOT EXISTS held_present ON public.held (present);"), cancellationToken);

    await Assert.That(await _workerStateAsync(worker, cancellationToken)).IsEqualTo("idle in transaction");
  }

  [Test]
  [Timeout(60000)]
  public async Task Guarded_AQuotedTableAndColumnThatExist_AreFound_AndTakeNoLockAsync(CancellationToken cancellationToken) {
    await using var worker = await _workerHoldingAsync("\"Odd Schema\".\"Held\"", cancellationToken);

    await _runWithLockTimeoutAsync(CatalogCheckedDdl.Guard(
      "ALTER TABLE \"Odd Schema\".\"Held\" ADD COLUMN IF NOT EXISTS \"Present\" int;"), cancellationToken);

    await Assert.That(await _workerStateAsync(worker, cancellationToken)).IsEqualTo("idle in transaction");
  }

  [Test]
  [Timeout(60000)]
  public async Task Guarded_AMissingColumnAndIndex_AreCreated_AsTheStatementsCreateThemAsync(CancellationToken cancellationToken) {
    await _executeAsync(CatalogCheckedDdl.Guard("""
      ALTER TABLE public.held ADD COLUMN IF NOT EXISTS added text NOT NULL DEFAULT 'x', ADD COLUMN IF NOT EXISTS present int;
      CREATE UNIQUE INDEX IF NOT EXISTS held_added ON public.held (id, added);
      ALTER TABLE "Odd Schema"."Held" ADD COLUMN IF NOT EXISTS "Added" int;
      """));

    await Assert.That(await _scalarAsync<bool>(
      "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'held' AND column_name = 'added')", cancellationToken)).IsTrue();
    await Assert.That(await _scalarAsync<bool>("SELECT to_regclass('public.held_added') IS NOT NULL", cancellationToken)).IsTrue();
    await Assert.That(await _scalarAsync<bool>(
      "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'Odd Schema' AND column_name = 'Added')", cancellationToken)).IsTrue();
  }

  [Test]
  [Timeout(60000)]
  public async Task Guarded_AMissingTable_FailsAsTheStatementFailsAsync(CancellationToken cancellationToken) {
    var failure = await Assert.That(async () => await _executeAsync(
        CatalogCheckedDdl.Guard("ALTER TABLE public.absent ADD COLUMN IF NOT EXISTS c int;")))
      .Throws<PostgresException>();

    await Assert.That(failure!.SqlState).IsEqualTo("42P01");
  }

  // ─────────────────────────────────────────────────────────────────────────

  /// <summary>A worker mid-transaction: it has read the table and written to it, and holds both locks until disposed.</summary>
  private async Task<NpgsqlConnection> _workerHoldingAsync(string table, CancellationToken cancellationToken) {
    var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(
      $"BEGIN; SELECT count(*) FROM {table}; UPDATE {table} SET id = id WHERE false;", connection);
    await command.ExecuteNonQueryAsync(cancellationToken);
    return connection;
  }

  private async Task<string> _workerStateAsync(NpgsqlConnection worker, CancellationToken cancellationToken) {
    var pid = worker.ProcessID;
    return await _scalarAsync<string>($"SELECT state FROM pg_stat_activity WHERE pid = {pid}", cancellationToken);
  }

  private async Task _runWithLockTimeoutAsync(string sql, CancellationToken cancellationToken) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand("SET lock_timeout = '2s'; " + sql, connection);
    await command.ExecuteNonQueryAsync(cancellationToken);
  }

  private async Task _executeAsync(string sql) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
  }

  private async Task<T> _scalarAsync<T>(string sql, CancellationToken cancellationToken = default) {
    await using var connection = new NpgsqlConnection(_database.ConnectionString);
    await connection.OpenAsync(cancellationToken);
    await using var command = new NpgsqlCommand(sql, connection);
    return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
  }
}
