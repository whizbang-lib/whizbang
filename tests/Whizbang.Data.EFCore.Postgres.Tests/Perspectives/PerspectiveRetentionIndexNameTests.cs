// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres.Schema;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// The retention index's name, derived at run time, is the one the schema generators give a declared
/// <c>[PerspectiveIndex(PerspectiveRowColumns.UpdatedAt)]</c> and gave every table before #2087: one index, under one
/// name, however it came to be asked for.
/// </summary>
/// <docs>fundamentals/perspectives/perspective-indexes#the-updated_at-index</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Schema/PerspectiveRetentionIndexes.cs</code-under-test>
[Category("Shard5")]
public class PerspectiveRetentionIndexNameTests {
  [Test]
  [Arguments("wh_per_order")]
  [Arguments("wh_per_a_very_long_perspective_table_name_that_pushes_the_index_name_past")]
  [Arguments("custom_table")]
  public async Task TheName_IsTheGeneratorsAsync(string table) {
    var generated = PostgresIdentifiers.WithinLimit($"idx_{table.Replace("wh_per_", "")}_updated_at");

    await Assert.That(PerspectiveRetentionIndexes.IndexName(table)).IsEqualTo(generated);
  }

  [Test]
  public async Task ALongName_IsShortenedWithinTheLimitAsync() {
    var name = PerspectiveRetentionIndexes.IndexName("wh_per_" + new string('x', 56));

    await Assert.That(name.Length).IsEqualTo(PostgresIdentifiers.LIMIT);
  }
}
