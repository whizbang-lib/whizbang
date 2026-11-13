// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using Npgsql;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Records every statement Npgsql runs against one database, from Npgsql's own tracing: what a set-based write test
/// counts to show how many round trips a batch costs. A count of statements, never of time.
/// </summary>
/// <remarks>
/// Each test owns its database (<see cref="EFCoreTestBase"/>), so filtering on the database name (<c>db.namespace</c>)
/// keeps out the statements of every test running alongside. Every Npgsql command is traced, whether it was issued
/// through Entity Framework or on the raw connection, so nothing a batch runs is missed.
/// </remarks>
internal sealed class NpgsqlStatementLog : IDisposable {
  private readonly ActivityListener _listener;
  private readonly ConcurrentQueue<string> _statements = new();
  private volatile bool _recording = true;

  public NpgsqlStatementLog(string connectionString) {
    var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
    _listener = new ActivityListener {
      ShouldListenTo = source => source.Name == "Npgsql",
      Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
      ActivityStopped = activity => {
        if (_recording
            && Equals(activity.GetTagItem("db.namespace"), database)
            && activity.GetTagItem("db.query.text") is string text) {
          _statements.Enqueue(text);
        }
      },
    };
    ActivitySource.AddActivityListener(_listener);
  }

  /// <summary>The statements recorded so far, in the order they finished.</summary>
  public IReadOnlyList<string> Statements => [.. _statements];

  /// <summary>Forgets what was recorded, so a test counts only what follows.</summary>
  public void Clear() => _statements.Clear();

  /// <summary>The recorded statements that contain every one of <paramref name="fragments"/>, ignoring case.</summary>
  /// <param name="fragments">Text each counted statement contains.</param>
  /// <returns>The matching statements.</returns>
  public IReadOnlyList<string> Matching(params string[] fragments) =>
    [.. Statements.Where(s => fragments.All(f => s.Contains(f, StringComparison.OrdinalIgnoreCase)))];

  /// <summary>Stops recording.</summary>
  public void Dispose() {
    _recording = false;
    _listener.Dispose();
  }
}
