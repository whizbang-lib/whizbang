// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Schema;

/// <summary>
/// The bootstrap's retry schedule and the exception a failure that is not transient ends it with (#1636). The
/// database-facing behavior (waiting on the schema lock, retrying a transient failure, failing loudly on any other)
/// is proven against a real server in <c>SchemaBootstrapPhaseTests</c>.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaBootstrapPhase.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/SchemaBootstrapException.cs</code-under-test>
[Category("Startup")]
public class SchemaBootstrapRetryTests {
  private static readonly int[] _expectedSeconds = [1, 2, 4, 8, 16, 30, 30, 30];

  /// <summary>
  /// The retry delay doubles from one second to a thirty-second cap, so a database that stays unavailable is asked at
  /// a bounded rate for as long as startup lasts.
  /// </summary>
  [Test]
  public async Task TheRetryDelay_DoublesFromOneSecond_ToAThirtySecondCapAsync() {
    TimeSpan[] delays = [.. Enumerable.Range(1, 8).Select(SchemaBootstrapPhase.RetryDelay)];

    await Assert.That(delays).IsEquivalentTo(
      [.. _expectedSeconds.Select(s => TimeSpan.FromSeconds(s))],
      TUnit.Assertions.Enums.CollectionOrdering.Matching);
    await Assert.That(SchemaBootstrapPhase.RetryDelay(int.MaxValue)).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  /// <summary>The exception names the schema and the step that failed, and carries why.</summary>
  [Test]
  public async Task TheException_NamesTheSchemaAndTheStep_AndCarriesTheCauseAsync() {
    var cause = new InvalidOperationException("permission denied for schema");

    var thrown = new SchemaBootstrapException("tenant_a", "173_RoleAssignments", cause);

    await Assert.That(thrown.Schema).IsEqualTo("tenant_a");
    await Assert.That(thrown.Step).IsEqualTo("173_RoleAssignments");
    await Assert.That(thrown.InnerException).IsSameReferenceAs(cause);
    await Assert.That(thrown.Message).Contains("tenant_a").And.Contains("173_RoleAssignments")
      .And.Contains("not transient").And.Contains("permission denied for schema");
  }

  /// <summary>The standard constructors name no schema or step.</summary>
  [Test]
  public async Task TheStandardConstructors_NameNoSchemaOrStepAsync() {
    var cause = new InvalidOperationException("cause");
    SchemaBootstrapException[] built = [new(), new("message"), new("message", cause)];

    foreach (var exception in built) {
      await Assert.That(exception.Schema).IsEqualTo("(unknown)");
      await Assert.That(exception.Step).IsEqualTo("(unknown)");
    }
    await Assert.That(built[0].Message).Contains("not transient");
    await Assert.That(built[1].Message).IsEqualTo("message");
    await Assert.That(built[2].InnerException).IsSameReferenceAs(cause);
  }
}
