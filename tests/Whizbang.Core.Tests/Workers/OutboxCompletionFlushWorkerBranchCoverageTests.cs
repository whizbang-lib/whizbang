// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The two options guards of <see cref="OutboxCompletionFlushWorker"/>: for each of its options, a
/// missing wrapper and a wrapper with no value are rejected at construction, naming the parameter.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/OutboxCompletionFlushWorker.cs</code-under-test>
[Category("Workers")]
public class OutboxCompletionFlushWorkerBranchCoverageTests {

  private sealed class NullValueOptions<T> : IOptions<T> where T : class {
    public T Value => null!;
  }

  private static OutboxCompletionFlushWorker _construct(
      IServiceScopeFactory scopeFactory,
      IOptions<OutboxCompletionFlushWorkerOptions> options,
      IOptions<WorkCoordinatorOptions> coordinatorOptions) =>
    new(
      scopeFactory: scopeFactory,
      schemaReadyGate: SchemaReadyGate.AlreadyReady(),
      options: options,
      coordinatorOptions: coordinatorOptions,
      logger: NullLogger<OutboxCompletionFlushWorker>.Instance,
      pinnedPool: NoOpPinnedConnectionPool.Instance);

  [Test]
  public async Task Constructor_NullOptions_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    await Assert.That(() => _construct(scopeFactory, null!, Options.Create(new WorkCoordinatorOptions())))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task Constructor_OptionsWithNullValue_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    await Assert.That(() => _construct(scopeFactory, new NullValueOptions<OutboxCompletionFlushWorkerOptions>(), Options.Create(new WorkCoordinatorOptions())))
      .Throws<ArgumentNullException>()
      .WithParameterName("options");
  }

  [Test]
  public async Task Constructor_NullCoordinatorOptions_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    await Assert.That(() => _construct(scopeFactory, Options.Create(new OutboxCompletionFlushWorkerOptions()), null!))
      .Throws<ArgumentNullException>()
      .WithParameterName("coordinatorOptions");
  }

  [Test]
  public async Task Constructor_CoordinatorOptionsWithNullValue_ThrowsArgumentNullExceptionAsync() {
    await using var sp = new ServiceCollection().BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    await Assert.That(() => _construct(scopeFactory, Options.Create(new OutboxCompletionFlushWorkerOptions()), new NullValueOptions<WorkCoordinatorOptions>()))
      .Throws<ArgumentNullException>()
      .WithParameterName("coordinatorOptions");
  }
}
