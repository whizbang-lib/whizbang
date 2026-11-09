// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Model = Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation.PhysicalJsonbContainmentSqlTests.JsonbColumnsModel;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// The containment rewrite still claims a filter after the physical field registry has been emptied, because the
/// shared model registers its columns again on every use.
/// </summary>
/// <remarks>
/// <para>
/// When the shared model registered its columns only once per process, a class that cleared the registry in between
/// left every later rewrite test reading the columns as document members: a whole-project run failed the rewrite tests
/// and their SQL twin while either passed alone.
/// </para>
/// <para>
/// Its own class since #2167: emptying the registry is the one thing here that disturbs other tests, so it runs alone,
/// and <see cref="PhysicalJsonbContainmentRewriterTests"/>, which only registers, runs in parallel.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-filters</docs>
[NotInParallel] // Clears the process-wide physical field registry the database tests read (#1903): runs alone.
[Category("Shard1")]
[SuppressMessage("Readability", "RCS1118:Mark local variable as const",
  Justification = "Captured on purpose: a const local is inlined as a literal and the parameterized path would go untested.")]
public class PhysicalJsonbContainmentRegistryResetTests {
  [Test]
  public async Task AfterTheFieldRegistryIsCleared_TheFilterIsStillClaimedAsync() {
    Model.Register();
    // What PhysicalFieldRegistryTests does before its tests, in the same process.
    PhysicalFieldRegistry.Clear();
    var tag = "red";

    await Assert.That(PhysicalJsonbContainmentRewriterTests.Containments(r => r.Data.Tags.Contains(tag))).IsEqualTo(1)
      .Because("registering the shared model has to put its columns back whenever the registry was cleared, or "
        + "the result of the rewrite tests depends on which classes ran before them");
  }
}
