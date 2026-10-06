// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The options guard of <see cref="LeaseRenewalWorker"/>: a missing options wrapper and a wrapper
/// with no value are both rejected at construction, naming the parameter.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/LeaseRenewalWorker.cs</code-under-test>
[Category("Workers")]
public class LeaseRenewalWorkerBranchCoverageTests {

  private sealed class NullValueOptions : IOptions<LeaseRenewalWorkerOptions> {
    public LeaseRenewalWorkerOptions Value => null!;
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new LeaseRenewalWorker(
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        schemaReadyGate: SchemaReadyGate.AlreadyReady(),
        options: null!,
        logger: NullLogger<LeaseRenewalWorker>.Instance,
        pinnedPool: NoOpPinnedConnectionPool.Instance))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new LeaseRenewalWorker(
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        schemaReadyGate: SchemaReadyGate.AlreadyReady(),
        options: new NullValueOptions(),
        logger: NullLogger<LeaseRenewalWorker>.Instance,
        pinnedPool: NoOpPinnedConnectionPool.Instance))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }
}
