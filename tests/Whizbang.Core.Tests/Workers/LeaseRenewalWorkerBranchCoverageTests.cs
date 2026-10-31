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
/// The constructor guards of <see cref="LeaseRenewalWorker"/>: a missing options wrapper, a wrapper
/// with no value and a missing instance provider are rejected at construction, naming the parameter.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/LeaseRenewalWorker.cs</code-under-test>
[Category("Workers")]
public class LeaseRenewalWorkerBranchCoverageTests {

  private sealed class NullValueOptions : IOptions<LeaseRenewalWorkerOptions> {
    public LeaseRenewalWorkerOptions Value => null!;
  }

  private static readonly Whizbang.Core.Observability.ServiceInstanceProvider _instance =
    new((Guid)Whizbang.Core.ValueObjects.TrackedGuid.New(), "svc", "host", 1);

  [Test]
  public async Task Constructor_NullInstanceProvider_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new LeaseRenewalWorker(
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        schemaReadyGate: SchemaReadyGate.AlreadyReady(),
        options: Microsoft.Extensions.Options.Options.Create(new LeaseRenewalWorkerOptions()),
        logger: NullLogger<LeaseRenewalWorker>.Instance,
        pinnedPool: NoOpPinnedConnectionPool.Instance,
        instanceProvider: null!))
      .Throws<ArgumentNullException>()
      .WithParameterName("instanceProvider");
  }

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    await Assert.That(() => new LeaseRenewalWorker(
        scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
        schemaReadyGate: SchemaReadyGate.AlreadyReady(),
        options: null!,
        logger: NullLogger<LeaseRenewalWorker>.Instance,
        pinnedPool: NoOpPinnedConnectionPool.Instance,
        instanceProvider: _instance))
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
        pinnedPool: NoOpPinnedConnectionPool.Instance,
        instanceProvider: _instance))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }
}
