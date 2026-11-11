// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The limits the worker pipeline computes from what varies from host to host (the processor count, the connection pool,
/// the gate) are derived settings, so the startup settings report shows each with the inputs it came from (#2251).
/// </summary>
public class WorkerDerivedSettingsTests {
  [Test]
  [Arguments(16, null, 128, "max(DefaultFloor, ProcessorCount x ThreadsPerProcessor), never below the current minimum")]
  [Arguments(2, null, 64, "max(DefaultFloor, ProcessorCount x ThreadsPerProcessor), never below the current minimum")]
  [Arguments(16, 300, 300, "the configured floor, never below the current minimum")]
  public async Task TheThreadPoolFloor_IsDerivedFromTheProcessorCountAsync(int processors, int? configured, int expected, string rule) {
    var floor = WorkerThreadPoolFloor.Derive(currentMinimum: 4, processors, configured);

    await Assert.That(floor.Name).IsEqualTo("Whizbang:Workers:ThreadPoolFloor");
    await Assert.That(floor.Value).IsEqualTo(expected);
    await Assert.That(floor.Value).IsEqualTo(WorkerThreadPoolFloor.Compute(4, processors, configured));
    await Assert.That(floor.Rule).IsEqualTo(rule);
    await Assert.That(floor.Inputs.Select(i => i.Name)).Contains("ProcessorCount");
    await Assert.That(WorkerThreadPoolFloor.Applied.Name).IsEqualTo("Whizbang:Workers:ThreadPoolFloor");
  }

  [Test]
  public async Task TheGatesReserve_IsDerivedFromItsPermits_AndAPoolSizedGateFromThePoolAsync() {
    using var configured = new WorkCoordinatorGate(50, NullLogger<WorkCoordinatorGate>.Instance, interactiveReserve: 7);
    using var defaulted = new WorkCoordinatorGate(50, NullLogger<WorkCoordinatorGate>.Instance);
    using var pooled = WorkCoordinatorGate.FromPoolSize(40, NullLogger<WorkCoordinatorGate>.Instance, reserve: 5);

    var reserve = defaulted.DerivedSettings.Single();
    await Assert.That(reserve.Name).IsEqualTo("Whizbang:WorkCoordinatorGate:InteractiveReserve");
    await Assert.That(reserve.DisplayValue).IsEqualTo("5");
    await Assert.That(reserve.Rule).IsEqualTo("MaxConcurrent / 10, leaving at least one shared permit");
    await Assert.That(configured.DerivedSettings.Single().Rule).IsEqualTo("configured, leaving at least one shared permit");
    var max = pooled.DerivedSettings.Single(d => d.Name == "Whizbang:WorkCoordinatorGate:MaxConcurrent");
    await Assert.That(max.ToString()).IsEqualTo("35 (MaxPoolSize - reserve, at least 1; MaxPoolSize=40, Reserve=5)");
    await Assert.That(pooled.MaxConcurrent).IsEqualTo(35);
  }

  [Test]
  public async Task ThePerspectiveWidth_AndTheInboxDispatchCount_AreClampedToTheGate_WithTheirInputsAsync() {
    var width = PerspectiveWorker.DeriveWidth(consumers: 4, width: 30, gateMaxConcurrent: 50);
    var dispatch = InboxDispatchWorker.DerivePartitionCount(configured: 80, gateMaxConcurrent: 50);

    await Assert.That(width.Name).IsEqualTo("Whizbang:Workers:Perspective:MaxConcurrentPerspectives");
    await Assert.That(width.Value).IsEqualTo(PerspectiveWorker.ClampWidthToGate(4, 30, 50));
    await Assert.That(width.Inputs.Select(i => i.Name)).IsEquivalentTo(["MaxConcurrentPerspectives", "MaxConcurrentDrainConsumers", "GateMaxConcurrent"]);
    await Assert.That(dispatch.Name).IsEqualTo("Whizbang:Workers:InboxDispatch:MaxConcurrentDispatch");
    await Assert.That(dispatch.Value).IsEqualTo(50);
    await Assert.That(dispatch.Inputs.Select(i => i.Name)).IsEquivalentTo(["MaxConcurrentDispatch", "GateMaxConcurrent"]);
  }

  [Test]
  public async Task TheWorkerPipeline_ReportsEveryDerivedLimit_ToTheStartupSettingsReportAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddWhizbangWorkers();
    await using var sp = services.BuildServiceProvider();

    var names = sp.GetServices<IDerivedSettingSource>().SelectMany(s => s.DerivedSettings).Select(d => d.Name).ToList();

    await Assert.That(names).Contains("Whizbang:Workers:ThreadPoolFloor");
    await Assert.That(names).Contains("Whizbang:WorkCoordinatorGate:InteractiveReserve");
    await Assert.That(names).Contains("Whizbang:Workers:Perspective:MaxConcurrentPerspectives");
    await Assert.That(names).Contains("Whizbang:Workers:InboxDispatch:MaxConcurrentDispatch");
    await Assert.That(names).Contains("Whizbang:BatchBuffer:MaxBufferedBytes");
  }
}
