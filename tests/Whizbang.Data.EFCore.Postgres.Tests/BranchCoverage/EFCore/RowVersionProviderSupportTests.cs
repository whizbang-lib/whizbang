// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="PerspectiveRowVersionSql.Supports"/>: PostgreSQL rows carry a
/// readable version and are guarded; any other provider is not. Neither context is opened.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PerspectiveRowVersionSql.cs</code-under-test>
[Category("Shard3")]
public class RowVersionProviderSupportTests {

  [Test]
  public async Task Supports_TrueForNpgsql_FalseForAnotherProviderAsync() {
    await using var npgsql = new ProviderContext(
      new DbContextOptionsBuilder<ProviderContext>().UseNpgsql("Host=localhost;Database=never_opened").Options);
    await using var inMemory = new ProviderContext(
      new DbContextOptionsBuilder<ProviderContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    await Assert.That(PerspectiveRowVersionSql.Supports(npgsql)).IsTrue();
    await Assert.That(PerspectiveRowVersionSql.Supports(inMemory)).IsFalse()
      .Because("only PostgreSQL rows carry the version the guard reads");
  }

  /// <summary>A DbContext that maps nothing and is never opened.</summary>
  public sealed class ProviderContext(DbContextOptions<ProviderContext> options) : DbContext(options);
}
