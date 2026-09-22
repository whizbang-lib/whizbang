using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="PgSearchPath.Apply"/>'s second no-op branch — a connection string that
/// already carries an explicit <c>Search Path</c>. No existing test exercises this branch (only the
/// null/blank-<c>searchPath</c> no-op and the apply-a-search-path branches are covered elsewhere).
/// Pure string manipulation — no database is used in this file.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/PgSearchPath.cs</code-under-test>
[Category("Shard1")]
public class PgSearchPathCoverageTests {

  // Operator config must win: if a connection string already pins a search path (an explicit
  // deployment choice) and this silently overwrote it with the resolver's guess, raw notification
  // components (signal transport, durable-signal tail, schedule claimer, poll sources) would query
  // against the wrong schema on a multi-schema deployment — resolving unqualified tables against
  // whatever schema the overwrite picked instead of the one the operator configured.
  [Test]
  public async Task Apply_ConnectionStringAlreadyHasSearchPath_ReturnsItUnchangedAsync() {
    const string original = "Host=coverage-host;Database=coverage_db;Search Path=already_set";

    var result = PgSearchPath.Apply(original, "different_schema");

    await Assert.That(result).IsEqualTo(original)
      .Because("an explicit Search Path already in the connection string is an operator choice — "
             + "it must win over the resolver's search path, not be silently overwritten");
  }
}
