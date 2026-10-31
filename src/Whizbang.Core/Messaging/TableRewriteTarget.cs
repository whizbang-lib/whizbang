// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// What a rewrite run by a program outside the process needs (<see cref="IWorkCoordinator.RewriteTableWithAsync"/>):
/// the table, qualified as the server names it, and the connection as the environment variables a libpq client reads.
/// </summary>
/// <remarks>
/// The environment carries the password when the connection has one, so <see cref="ToString"/> names only the table.
/// </remarks>
/// <param name="qualifiedTable">The schema-qualified table, quoted where the server needs it.</param>
/// <param name="clientEnvironment">The connection as libpq environment variables (<c>PGHOST</c>, <c>PGPORT</c>, <c>PGDATABASE</c>, <c>PGUSER</c>, <c>PGPASSWORD</c> and the rest).</param>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/PgRepackTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ExternalTableRewriteTests.cs</tests>
public sealed class TableRewriteTarget(string qualifiedTable, IReadOnlyDictionary<string, string> clientEnvironment) {
  /// <summary>The schema-qualified table.</summary>
  public string QualifiedTable { get; } = qualifiedTable;

  /// <summary>The connection as libpq environment variables.</summary>
  public IReadOnlyDictionary<string, string> ClientEnvironment { get; } = clientEnvironment;

  /// <summary>The table; never the connection.</summary>
  /// <returns>The qualified table.</returns>
  public override string ToString() => QualifiedTable;
}
