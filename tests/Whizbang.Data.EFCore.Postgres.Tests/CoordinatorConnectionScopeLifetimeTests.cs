using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The EF Core path of <see cref="CoordinatorConnectionScope"/> opens the DbContext's connection when it
/// finds it closed. EF Core only closes connections it opened itself, so a scope that left the connection
/// open kept one pooled connection checked out for the DbContext's whole life: one per DI scope that made
/// a single coordinator call, which under a wide perspective drain is the whole pool. The scope now returns
/// what it opened and leaves alone what the caller had open.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorConnectionScope.cs</code-under-test>
[Category("Shard3")]
public class CoordinatorConnectionScopeLifetimeTests : EFCoreTestBase {
  [Test]
  public async Task AcquireForEfCore_OpenedTheConnection_ReturnsItToThePoolWhenTheScopeEndsAsync() {
    await using var ctx = CreateDbContext();
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    await Assert.That(connection.State).IsEqualTo(ConnectionState.Closed)
      .Because("a fresh DbContext has not opened its connection yet");

    await using (var scope = await CoordinatorConnectionScope.AcquireForEfCoreAsync(connection, CancellationToken.None)) {
      await Assert.That(scope.Connection.State).IsEqualTo(ConnectionState.Open);
    }

    await Assert.That(connection.State).IsEqualTo(ConnectionState.Closed)
      .Because("the scope opened it, so the scope returns it to the pool; EF Core never closes a connection it did not open");
  }

  [Test]
  public async Task AcquireForEfCore_ConnectionAlreadyOpen_LeavesItOpenWhenTheScopeEndsAsync() {
    await using var ctx = CreateDbContext();
    await ctx.Database.OpenConnectionAsync();
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    try {
      await using (var scope = await CoordinatorConnectionScope.AcquireForEfCoreAsync(connection, CancellationToken.None)) {
        await Assert.That(ReferenceEquals(scope.Connection, connection)).IsTrue();
      }
      await Assert.That(connection.State).IsEqualTo(ConnectionState.Open)
        .Because("the caller opened it; whatever the caller is doing on it continues after the scope");
    } finally {
      await ctx.Database.CloseConnectionAsync();
    }
  }

  [Test]
  public async Task AcquireForEfCore_UnderACallerTransaction_TheTransactionSurvivesTheScopeAsync() {
    await using var ctx = CreateDbContext();
    await ctx.Database.OpenConnectionAsync();
    var connection = (NpgsqlConnection)ctx.Database.GetDbConnection();
    try {
      await using var transaction = await connection.BeginTransactionAsync();
      await using (var scope = await CoordinatorConnectionScope.AcquireForEfCoreAsync(connection, CancellationToken.None)) {
        await using var probe = scope.Connection.CreateCommand();
        probe.CommandText = "SELECT 1";
        probe.Transaction = transaction;
        await probe.ExecuteScalarAsync();
      }
      await using var after = connection.CreateCommand();
      after.CommandText = "SELECT 2";
      after.Transaction = transaction;
      await Assert.That((int)(await after.ExecuteScalarAsync())!).IsEqualTo(2)
        .Because("a transaction begun by the caller is still usable after a scope that did not open the connection");
      await transaction.CommitAsync();
    } finally {
      await ctx.Database.CloseConnectionAsync();
    }
  }
}
