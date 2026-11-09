// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// The heartbeat starts at registration (#2126), and the core registration is where that is decided: the worker
/// pipeline hosts the heartbeat at <see cref="HeartbeatWorker.StartsAt"/>, which is <see cref="StartupStage.Elect"/>,
/// the first stage after Preflight, whose last step registers the instance. Moved later, a starting instance would
/// again be judged by a heartbeat it does not send, and a live peer's reap would evict it during a long Migrate.
/// </summary>
/// <docs>operations/startup/startup-status#what-runs-in-each-stage</docs>
/// <code-under-test>src/Whizbang.Core/Workers/WorkerPipelineExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Workers/HeartbeatWorker.cs</code-under-test>
[Category("Startup")]
public class HeartbeatStartPointTests {
  [Test]
  public async Task TheHeartbeat_StartsAtElect_TheFirstStageAfterRegistrationAsync() {
    await Assert.That(HeartbeatWorker.StartsAt).IsEqualTo(StartupStage.Elect)
      .Because("registration is Preflight's last step; the heartbeat must start right after it, not at schema-ready");
    await Assert.That(StartupStages.Startup.SkipWhile(s => s != StartupStage.Preflight).Skip(1).First())
      .IsEqualTo(HeartbeatWorker.StartsAt);
  }

  [Test]
  public async Task TheWorkerPipeline_HostsTheHeartbeatAtItsStartPoint_AndNowhereElseAsync() {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddWhizbangWorkers();
    var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
    await using var provider = services.BuildServiceProvider();

    var staged = hosted.Select(d => d.ImplementationFactory)
      .OfType<Func<IServiceProvider, StartupStageGatedService<HeartbeatWorker>>>().ToList();
    var atSchemaReady = hosted.Select(d => d.ImplementationFactory)
      .OfType<Func<IServiceProvider, SchemaReadyGatedService<HeartbeatWorker>>>().ToList();

    await Assert.That(atSchemaReady).IsEmpty()
      .Because("#2126 replaced the schema-ready start point: the heartbeat starts once the instance is registered");
    await Assert.That(staged.Count).IsEqualTo(1);
    var wrapper = staged[0](provider);
    await Assert.That(wrapper.Stage).IsEqualTo(HeartbeatWorker.StartsAt);
    await Assert.That(wrapper.Inner).IsSameReferenceAs(provider.GetRequiredService<HeartbeatWorker>());
  }
}
