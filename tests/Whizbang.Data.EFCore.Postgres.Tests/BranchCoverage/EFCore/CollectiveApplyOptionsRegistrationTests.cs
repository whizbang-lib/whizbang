// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for the collective apply policy <c>AddCollectiveEventsEFCore</c> registers: the
/// framework default when the host registered no <see cref="PostgresOptions"/>, and the host's
/// batching and statement timeout when it did.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/CollectiveEventsEFCoreExtensions.cs</code-under-test>
[Category("Shard5")]
public class CollectiveApplyOptionsRegistrationTests {

  [Test]
  public async Task ApplyOptions_DefaultWithoutPostgresOptions_HostValuesWithThemAsync() {
    var unconfigured = new ServiceCollection();
    unconfigured.AddCollectiveEventsEFCore<PolicyContext>([]);
    await using var unconfiguredProvider = unconfigured.BuildServiceProvider();

    var configured = new ServiceCollection();
    configured.AddSingleton(Options.Create(new PostgresOptions {
      CollectiveApplyBatchSize = 7,
      CollectiveApplyStatementTimeoutSeconds = 9,
    }));
    configured.AddCollectiveEventsEFCore<PolicyContext>([]);
    await using var configuredProvider = configured.BuildServiceProvider();

    var fallback = unconfiguredProvider.GetRequiredService<CollectiveApplyOptions>();
    var custom = configuredProvider.GetRequiredService<CollectiveApplyOptions>();

    await Assert.That(fallback).IsSameReferenceAs(CollectiveApplyOptions.Default);
    await Assert.That(custom.BatchSize).IsEqualTo(7);
    await Assert.That(custom.StatementTimeoutSeconds).IsEqualTo(9);
  }

  /// <summary>A DbContext type argument; it is never constructed here.</summary>
  public sealed class PolicyContext(DbContextOptions<PolicyContext> options) : DbContext(options);
}
