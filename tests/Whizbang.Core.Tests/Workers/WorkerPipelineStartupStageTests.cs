// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;
using Whizbang.Core.RunControl;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The worker pipeline wires the startup stage (#1636): one reporter, the run-control participant that moves it with
/// the lifecycle, the host-lifecycle service that marks Preflight, Draining and Stopped, and the health surfaces that
/// report it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Health/WhizbangHealthServiceCollectionExtensions.cs</code-under-test>
[Category("Startup")]
public sealed class WorkerPipelineStartupStageTests {
  [Test]
  public async Task AddWhizbangWorkers_RegistersOneReporter_TheParticipant_AndTheHostLifecycleServiceAsync() {
    var services = _compose(times: 2);

    await Assert.That(services.Count(d => d.ServiceType == typeof(IStartupStageReporter))).IsEqualTo(1);
    await Assert.That(services.Count(d => d.ServiceType == typeof(IHostedService)
      && d.ImplementationType == typeof(StartupStageHostLifecycle))).IsEqualTo(1);

    await using var provider = services.BuildServiceProvider();
    await Assert.That(provider.GetRequiredService<IStartupStageReporter>()).IsTypeOf<StartupStageReporter>();
    await Assert.That(provider.GetServices<IWhizbangRunControl>().OfType<StartupStageRunControl>().Count()).IsEqualTo(1)
      .Because("the stage moves with the lifecycle's AcceptingCommands, Running, Stopping and Stopped transitions");
  }

  [Test]
  public async Task AStageEntered_IsWhatTheHealthSurfacesReportAsync() {
    await using var provider = _compose(times: 1).BuildServiceProvider();
    await provider.GetRequiredService<IStartupStageReporter>().EnterAsync(StartupStage.Preflight, "host starting");

    var readiness = await provider.GetRequiredService<WhizbangHealthAggregator>()
      .EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);

    await Assert.That(readiness.Stage!.Stage).IsEqualTo(StartupStage.Preflight);
    await Assert.That(readiness.Components.Single(c => c.Component == "startup").Detail)
      .StartsWith("Stage 1 of 7: Preflight - host starting; ");
  }

  private static ServiceCollection _compose(int times) {
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddLogging();
    for (var i = 0; i < times; i++) {
      services.AddWhizbangWorkers();
    }
    return services;
  }
}
