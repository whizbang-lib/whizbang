// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// A timeout belongs to the connection it applies to. These lock the two budgets the framework owns —
/// the work coordinator's and schema initialization's — against the connection string they override.
/// </summary>
/// <remarks>
/// <para>
/// The coordinator budget existed on the EF Core path and not the Dapper one: Dapper's claim and commit
/// commands took the connection string's <c>Command Timeout</c> (30 s by Npgsql default) and the
/// maintenance and purge commands hard-coded 30 s. Commit batches have been observed at 13-30 s under a
/// bulk-import backlog, and a cancelled commit loses its completions: the rows re-claim as lease expiries
/// and the poison gate throttles the drain to one row per cycle. Both coordinators now read one constant.
/// </para>
/// <para>
/// Schema DDL used a fixed ten minutes and ignored the <c>-init</c> connection string's own
/// <c>Command Timeout</c>, so a migration that needed longer could not be given longer.
/// </para>
/// <para>
/// The expected seconds are written as literals rather than as the constants under test, so a silent
/// change to either number fails here. The numbers are judgements about the worst legitimate batch and the
/// worst legitimate migration, and drifting one of them quietly is the class of defect this area is about.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/CoordinatorCommandBudget.cs</code-under-test>
[Category("Unit")]
public class CommandBudgetTests {

  [Test]
  public async Task CoordinatorBudget_OverridesWhateverTheConnectionStringCarriedAsync() {
    var builder = new NpgsqlConnectionStringBuilder {
      Host = "localhost",
      Database = "x",
      CommandTimeout = 1,
    };
    await using var connection = new NpgsqlConnection(builder.ConnectionString);

    await using var plain = connection.CreateCommand();
    await using var budgeted = CoordinatorCommandBudget.Apply(connection.CreateCommand());

    await Assert.That(plain.CommandTimeout).IsEqualTo(1)
      .Because("a raw command inherits the connection string, which is the hole the budget closes.");
    await Assert.That(budgeted.CommandTimeout).IsEqualTo(180)
      .Because("three minutes: the worst legitimate commit batch is ~30 s, and a cancelled commit loses "
        + "its completions.");
  }

  [Test]
  public async Task CoordinatorBudget_ReturnsTheSameCommandSoItComposesAsync() {
    await using var connection = new NpgsqlConnection("Host=localhost;Database=x");
    await using var command = connection.CreateCommand();

    await Assert.That(CoordinatorCommandBudget.Apply(command)).IsSameReferenceAs(command);
  }

  [Test]
  public async Task CoordinatorBudget_RejectsNullAsync() =>
    await Assert.That(() => CoordinatorCommandBudget.Apply<NpgsqlCommand>(null!))
      .Throws<ArgumentNullException>();

  [Test]
  [Arguments(null, 600)]
  [Arguments("", 600)]
  [Arguments("   ", 600)]
  [Arguments("Host=localhost;Database=x", 600)]
  [Arguments("Host=localhost;Database=x;Command Timeout=1200", 1200)]
  [Arguments("Host=localhost;Database=x;CommandTimeout=900", 900)]
  [Arguments("Host=localhost;Database=x;Command Timeout=0", 600)]
  [Arguments("this is not a connection string at all", 600)]
  public async Task SchemaBudget_FollowsTheInitConnectionStringAsync(string? connectionString, int expected) =>
    await Assert.That(SchemaCommandBudget.ForInitConnection(connectionString)).IsEqualTo(expected);

  [Test]
  public async Task SchemaBudget_AnAbsentKeywordDoesNotFallToNpgsqlsThirtySecondsAsync() {
    // The trap this guards: NpgsqlConnectionStringBuilder reports CommandTimeout = 30 for a string that
    // never mentioned it, so reading the property without asking whether the keyword is present would
    // shorten every DDL statement from ten minutes to thirty seconds.
    var builder = new NpgsqlConnectionStringBuilder("Host=localhost;Database=x");

    await Assert.That(builder.CommandTimeout).IsEqualTo(30)
      .Because("the builder's default is what makes the naive read wrong.");
    await Assert.That(SchemaCommandBudget.ForInitConnection(builder.ConnectionString)).IsEqualTo(600);
  }
}
