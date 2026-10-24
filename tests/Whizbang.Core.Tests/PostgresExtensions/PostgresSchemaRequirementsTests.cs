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
    var requirements = PostgresSchemaRequirements.From(["wh_per_product.embedding", "wh_per_order.summary_vector"], []);

    await Assert.That(requirements.Select(r => r.ToString())).IsEquivalentTo([
      "Vector fields on perspectives (wh_per_product.embedding, explicit)",
      "Vector fields on perspectives (wh_per_order.summary_vector, explicit)",
    ]);
    await Assert.That(requirements.All(r => r.Requirement.Capability == new CapabilityId("postgres", "vector"))).IsTrue();
  }

  [Test]
  public async Task NoVectorColumns_RequireNothingAsync() {
    await Assert.That(PostgresSchemaRequirements.From([], [])).IsEmpty();
    await Assert.That(() => PostgresSchemaRequirements.From(null!, [])).Throws<ArgumentNullException>();
    await Assert.That(() => PostgresSchemaRequirements.From([], null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task EachPerspectiveWithAnOptionalExtensionBlock_RequiresItsExtensionImplicitlyAsync() {
    (string Name, string Sql)[] perspectives = [
      ("wh_per_product", "CREATE TABLE x (a text);\n" + OptionalExtensionBlocks.BEGIN_MARKER + "pg_trgm\n"
        + "CREATE INDEX IF NOT EXISTS idx_a ON x USING gin (a gin_trgm_ops);\n" + OptionalExtensionBlocks.END_MARKER + "\n"
        + OptionalExtensionBlocks.BEGIN_MARKER + "pg_trgm\nCREATE INDEX IF NOT EXISTS idx_b ON x USING gin (b gin_trgm_ops);\n"
        + OptionalExtensionBlocks.END_MARKER + "\n"),
      ("wh_per_plain", "CREATE TABLE y (a text);"),
      ("wh_per_custom", OptionalExtensionBlocks.BEGIN_MARKER + "wh_custom_ext\nCREATE INDEX i ON z (a);\n" + OptionalExtensionBlocks.END_MARKER),
    ];

    var requirements = PostgresSchemaRequirements.From([], perspectives);

    await Assert.That(requirements.Select(r => $"{r.Requirement.Capability}: {r}")).IsEquivalentTo([
      "postgres/pg_trgm: Substring indexes on perspective fields (wh_per_product, default)",
      "postgres/wh_custom_ext: Indexes that need wh_custom_ext (wh_per_custom, default)",
    ]).Because("a skipped block degrades the perspective's queries, so its extension is an implicit requirement, once per perspective");
  }

  [Test]
  [Arguments("0A000", PostgresExtensionState.NotAllowlisted)]
  [Arguments("42501", PostgresExtensionState.NoPrivilege)]
  [Arguments("58P01", PostgresExtensionState.Missing)]
  public async Task ARefusal_ReadsAsThePostgresStateItReportsAsync(string sqlState, PostgresExtensionState expected) {
    await Assert.That(PostgresRequiredExtension.StateOfRefusal(sqlState)).IsEqualTo(expected);
  }
}
