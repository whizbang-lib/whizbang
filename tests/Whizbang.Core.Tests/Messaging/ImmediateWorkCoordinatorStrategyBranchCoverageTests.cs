// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.SystemEvents;
using Whizbang.Core.Tests.Observability;
using Whizbang.Core.Tests.Workers;
using Whizbang.Core.Tracing;
using Whizbang.Testing.Options;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Branch coverage for <see cref="ImmediateWorkCoordinatorStrategy.FlushAndGetBatchAsync"/> with
/// work-coordinator metrics wired: every flush is counted, tagged with the strategy and the API
/// trigger, so a dashboard can tell immediate flushes from the other strategies'.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ImmediateWorkCoordinatorStrategy.cs</code-under-test>
public class ImmediateWorkCoordinatorStrategyBranchCoverageTests {

  [Test]
  public async Task FlushAsync_WithMetrics_CountsTheFlushTaggedAsImmediateApiAsync() {
    using var factory = new TestMeterFactory();
    var metrics = new WorkCoordinatorMetrics(new WhizbangMetrics(factory));
    using var helper = new MetricAssertionHelper(factory.CreatedMeters.First(m => m.Name == WorkCoordinatorMetrics.METER_NAME));
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var sut = new ImmediateWorkCoordinatorStrategy(
      coordinator: new NoOpWorkCoordinator(),
      instanceProvider: new ServiceInstanceProvider(configuration: new ConfigurationBuilder().Build()),
      options: new WorkCoordinatorOptions(),
      logger: NullLogger<ImmediateWorkCoordinatorStrategy>.Instance,
      scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
      lifecycleMessageDeserializer: new JsonLifecycleMessageDeserializer(),
      tracingOptions: new StaticOptionsMonitor<TracingOptions>(new TracingOptions()),
      deferredChannel: new DeferredOutboxChannel(),
      systemEventOptions: Options.Create(new SystemEventOptions()),
      workChannelWriter: new WorkChannelWriter(),
      metrics: metrics);

    await sut.FlushAsync(WorkBatchOptions.None);

    var counted = helper.GetByName("whizbang.work_coordinator.flush.calls")
      .Where(m => m.Value > 0)
      .ToList();
    await Assert.That(counted.Count).IsEqualTo(1)
      .Because("one flush call is one count, recorded in one tagged series");
    await Assert.That(counted[0].Value).IsEqualTo(1);
    await Assert.That(counted[0].Tags["strategy"]).IsEqualTo("immediate");
    await Assert.That(counted[0].Tags["trigger"]).IsEqualTo("api");
  }
}
