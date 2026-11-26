// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Perspectives;

/// <summary>
/// Asks the server for the name of the database a context's connection reaches the first time a connection to it opens,
/// so a strongly-typed id a context writes inside a document, by any route (an <c>ExecuteUpdate</c> included), follows the
/// gate of the schema as the server names it, not as the connection string does (#2518, <see cref="PostgresServerDatabase"/>).
/// Once known, an open costs a dictionary lookup.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdPooledDatabaseNameTests.cs</tests>
internal sealed class ServerDatabaseNameInterceptor : DbConnectionInterceptor {
  /// <summary>The one instance every context shares.</summary>
  internal static readonly ServerDatabaseNameInterceptor Instance = new();

  private ServerDatabaseNameInterceptor() { }

  /// <inheritdoc/>
  public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
    PostgresServerDatabase.Resolve(connection);

  /// <inheritdoc/>
  public override async Task ConnectionOpenedAsync(
      DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default) =>
    await PostgresServerDatabase.ResolveAsync(connection, cancellationToken).ConfigureAwait(false);
}
