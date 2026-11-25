// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// #2573: on the Dapper driver too, the one-off restoration of events written here and stored under another origin is
/// registered as a maintenance step.
/// </summary>
/// <docs>resilience/stream-integrity#events-written-here-returned</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/WrittenHereOriginRestorationStep.cs</code-under-test>
public class DapperWrittenHereOriginRestorationTests : PostgresTestBase {
  [Test]
  public async Task TheDapperDriver_RegistersTheRestorationStepAsync() {
    var services = new ServiceCollection().AddWhizbangPostgres(
      ConnectionString, Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions(), false, (string?)null, null);
    services.AddLogging();
    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();

    var steps = scope.ServiceProvider.GetServices<IMaintenanceStep>().OfType<WrittenHereOriginRestorationStep>().ToList();

    await Assert.That(steps.Count).IsEqualTo(1);
  }
}
