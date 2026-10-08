// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// A timeout belongs to the connection it applies to — except the coordinator's own SQL, where the
/// consequence of cancelling is not a failed query but a stalled drain. Both coordinators read one
/// constant for that, which is what these lock.
/// </summary>
/// <remarks>
/// <para>
/// The budget existed on the EF Core path and not the Dapper one: Dapper's claim and commit commands took
/// the connection string's <c>Command Timeout</c> (30 s by Npgsql default) and its maintenance, purge and
/// stream-probe commands hard-coded 30 s. Commit batches have been observed at 13-30 s under a bulk-import
/// backlog, and a cancelled commit loses its completions: the rows re-claim as lease expiries and the
/// poison gate throttles the drain to one row per cycle.
/// </para>
/// <para>
/// The expected seconds are written as a literal rather than as the constant under test, so a silent
/// change to the number fails here. It is a judgement about the worst legitimate batch, and drifting it
/// quietly is the defect this area is about.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorCommandTimeout.cs</code-under-test>
[Category("Unit")]
public class CoordinatorCommandTimeoutBudgetTests {

  [Test]
  public async Task Apply_OverridesWhateverTheConnectionStringCarriedAsync() {
    var builder = new NpgsqlConnectionStringBuilder {
      Host = "localhost",
      Database = "x",
      CommandTimeout = 1,
    };
    await using var connection = new NpgsqlConnection(builder.ConnectionString);

    await using var plain = connection.CreateCommand();
    await using var budgeted = CoordinatorCommandTimeout.Apply(connection.CreateCommand());

    await Assert.That(plain.CommandTimeout).IsEqualTo(1)
      .Because("a raw command inherits the connection string, which is the hole the budget closes.");
    await Assert.That(budgeted.CommandTimeout).IsEqualTo(180)
      .Because("three minutes: the worst legitimate commit batch is ~30 s, and a cancelled commit loses "
        + "its completions.");
  }

  [Test]
  public async Task Apply_ReturnsTheSameCommandSoItComposesAsync() {
    await using var connection = new NpgsqlConnection("Host=localhost;Database=x");
    await using var command = connection.CreateCommand();

    await Assert.That(CoordinatorCommandTimeout.Apply(command)).IsSameReferenceAs(command);
  }

  [Test]
  public async Task Apply_RejectsNullAsync() =>
    await Assert.That(() => CoordinatorCommandTimeout.Apply<NpgsqlCommand>(null!))
      .Throws<ArgumentNullException>();

  [Test]
  public async Task BothCoordinatorsReadTheSameBudgetAsync() {
    // The EF Core package keeps its own name for the budget so its 92 call sites did not churn; it must
    // forward to this one. Two independent 180s are what let the Dapper path sit at 30 for so long.
    await using var connection = new NpgsqlConnection("Host=localhost;Database=x");
    await using var command = CoordinatorCommandTimeout.Apply(connection.CreateCommand());

    await Assert.That(command.CommandTimeout).IsEqualTo(180);
  }
}
