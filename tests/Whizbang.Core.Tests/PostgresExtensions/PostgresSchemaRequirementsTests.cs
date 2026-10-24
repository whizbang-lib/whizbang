// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.PostgresExtensions;

/// <summary>
/// The capabilities a context's schema requires, read from what its generated schema declares: pgvector for every
/// vector column, explicitly, because the column cannot exist without it.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresSchemaRequirements.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresRequiredExtension.cs</code-under-test>
/// <docs>fundamentals/perspectives/vector-fields#pgvector-extension</docs>
public class PostgresSchemaRequirementsTests {
  [Test]
  public async Task EachVectorColumn_RequiresPgvectorExplicitlyAsync() {
    var requirements = PostgresSchemaRequirements.From(["wh_per_product.embedding", "wh_per_order.summary_vector"]);

    await Assert.That(requirements.Select(r => r.ToString())).IsEquivalentTo([
      "Vector fields on perspectives (wh_per_product.embedding, explicit)",
      "Vector fields on perspectives (wh_per_order.summary_vector, explicit)",
    ]);
    await Assert.That(requirements.All(r => r.Requirement.Capability == new CapabilityId("postgres", "vector"))).IsTrue();
  }

  [Test]
  public async Task NoVectorColumns_RequireNothingAsync() {
    await Assert.That(PostgresSchemaRequirements.From([])).IsEmpty();
    await Assert.That(() => PostgresSchemaRequirements.From(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  [Arguments("0A000", PostgresExtensionState.NotAllowlisted)]
  [Arguments("42501", PostgresExtensionState.NoPrivilege)]
  [Arguments("58P01", PostgresExtensionState.Missing)]
  public async Task ARefusal_ReadsAsThePostgresStateItReportsAsync(string sqlState, PostgresExtensionState expected) {
    await Assert.That(PostgresRequiredExtension.StateOfRefusal(sqlState)).IsEqualTo(expected);
  }
}
