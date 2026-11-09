// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// #2194: a database one complete schema pass has built reads as current on the next start. The stored-form conversions
/// run before the pass creates the perspective tables, so on a new database they record nothing about them; a table with
/// no row and no record has nothing to convert, so the next start's Assess reads Current instead of running a pass it
/// does not need.
/// </summary>
/// <remarks>
/// The run's schema template is exactly that database: one <c>EnsureWhizbangDatabaseInitializedAsync</c> on an empty
/// one (<see cref="EFCoreSchemaTemplate"/>), and each test gets a copy of it.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/StoredFormSettlement.cs</code-under-test>
[Category("Integration")]
[Category("Shard4")]
public class OnePassReadsCurrentTests : EFCoreTestBase {
  [Test]
  [Timeout(120000)]
  public async Task ADatabaseOnePassBuilt_ReadsAsCurrentOnTheNextStartAsync(CancellationToken ct) {
    await using var context = CreateDbContext();
    // The premise: the model declares conversions on tables only the pass creates, so the conversions that run before
    // the pass find none of them. Without one, this test would prove nothing.
    var declared = CanonicalTemporalRewrite.ForModel(context.Model, PerspectiveDocumentSerialization.Options, "public").Length
      + DeclaredKeyShapes.ForModel(context.Model, PerspectiveDocumentSerialization.Options).Length;
    await Assert.That(declared).IsGreaterThan(0);

    await Assert.That(await context.IsWhizbangSchemaCurrentAsync(ct)).IsTrue()
      .Because("one complete pass built every table, and a table with no row has nothing to convert");
  }
}
