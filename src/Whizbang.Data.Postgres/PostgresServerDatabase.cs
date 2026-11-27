// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Data.Common;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The name the server gives the database a connection reaches (<c>current_database()</c>), which keys a schema's
/// strong-id document gate in this process (#2518).
/// </summary>
/// <remarks>
/// <para>
/// A connection string's database name is what the client asked for, not what it reached: behind a pooler such as
/// pgbouncer it is an alias, and a coordinator can use a connection string of its own. Keyed on that name, the gate's
/// check and a writer could name the same schema differently, so the writer would never see the gate open, keep writing
/// the object form, say so in its heartbeat, and the stored-form rewrite would never start. The server's name is the same
/// from every connection that reaches the database.
/// </para>
/// <para>
/// The name is asked once for each host and requested database (<see cref="DbConnection.DataSource"/> and
/// <see cref="DbConnection.Database"/>), on the first open connection that reaches them, and kept for the life of the
/// process. Until then a connection is known by a provisional name (<see cref="KeyOf"/>) that no gate opens, so a write
/// through it takes the object form every release reads; a hold recorded under that name moves to the server's name once
/// it is known.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdPooledDatabaseNameTests.cs</tests>
public static class PostgresServerDatabase {
  private static readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);

  /// <summary>
  /// The name a connection's database is known by in this process: the server's name once it was asked, a provisional
  /// name no gate opens until then.
  /// </summary>
  /// <param name="connection">The connection, open or not.</param>
  /// <returns>The name.</returns>
  public static string KeyOf(DbConnection connection) {
    ArgumentNullException.ThrowIfNull(connection);
    var requested = _requested(connection);
    return _names.TryGetValue(requested, out var name) ? name : _provisional(requested);
  }

  /// <summary>
  /// Asks the server for the name of the database an open connection reaches, once for each host and requested database,
  /// and moves any hold recorded under the provisional name to it.
  /// </summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The server's name for the database.</returns>
  public static async ValueTask<string> ResolveAsync(DbConnection connection, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    var requested = _requested(connection);
    if (_names.TryGetValue(requested, out var known)) {
      return known;
    }
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT current_database()";
    var name = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    return _record(requested, name);
  }

  /// <summary>The synchronous <see cref="ResolveAsync"/>, for a connection opened synchronously.</summary>
  /// <param name="connection">An open connection.</param>
  /// <returns>The server's name for the database.</returns>
  public static string Resolve(DbConnection connection) {
    ArgumentNullException.ThrowIfNull(connection);
    var requested = _requested(connection);
    if (_names.TryGetValue(requested, out var known)) {
      return known;
    }
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT current_database()";
    return _record(requested, (string)command.ExecuteScalar()!);
  }

  private static string _record(string requested, string name) {
    var recorded = _names.GetOrAdd(requested, name);
    WhizbangIdDocumentForm.Rename(_provisional(requested), recorded);
    return recorded;
  }

  private static string _requested(DbConnection connection) => connection.DataSource + "|" + connection.Database;

  private static string _provisional(string requested) => "?" + requested;
}
