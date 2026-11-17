// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// An open Npgsql connection as a library that creates its own commands sees it: every command it creates is an
/// attributed one (<see cref="WhizbangCommand.CreateWhizbangCommand(NpgsqlConnection)"/>), so a statement Dapper sends
/// for the framework leads with its mechanism and is recorded like every other.
/// </summary>
/// <remarks>
/// Npgsql's connection is sealed and its command creation is not virtual, so the only place to reach the commands
/// Dapper creates is the connection Dapper is handed. Everything else forwards to the connection. Disposing this
/// disposes nothing: the connection belongs to whoever acquired it.
/// </remarks>
/// <param name="inner">The open connection the commands run on.</param>
/// <docs>operations/observability/database-attribution</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperCommandAttributionTests.cs</tests>
internal sealed class AttributedDbConnection(NpgsqlConnection inner) : DbConnection {
  /// <summary>The connection the commands run on.</summary>
  internal NpgsqlConnection Inner { get; } = inner;

  /// <inheritdoc />
  [AllowNull]
  public override string ConnectionString {
    get => Inner.ConnectionString;
    set => Inner.ConnectionString = value;
  }

  /// <inheritdoc />
  public override string Database => Inner.Database;

  /// <inheritdoc />
  public override string DataSource => Inner.DataSource;

  /// <inheritdoc />
  public override string ServerVersion => Inner.ServerVersion;

  /// <inheritdoc />
  public override ConnectionState State => Inner.State;

  /// <inheritdoc />
  public override void ChangeDatabase(string databaseName) => Inner.ChangeDatabase(databaseName);

  /// <inheritdoc />
  public override void Close() => Inner.Close();

  /// <inheritdoc />
  public override void Open() => Inner.Open();

  /// <inheritdoc />
  public override Task OpenAsync(CancellationToken cancellationToken) => Inner.OpenAsync(cancellationToken);

  /// <inheritdoc />
  protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => Inner.BeginTransaction(isolationLevel);

  /// <inheritdoc />
  protected override DbCommand CreateDbCommand() => Inner.CreateWhizbangCommand();
}
