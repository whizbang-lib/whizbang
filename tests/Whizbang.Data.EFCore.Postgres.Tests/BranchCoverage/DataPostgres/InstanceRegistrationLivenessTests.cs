// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.DataPostgres;

/// <summary>
/// What a registration records for the one liveness rule (#2126): the instance's connection mode and the liveness window
/// its heartbeat cadence implies, read from the host's services the way the heartbeat reads them, so the registration
/// and the first beat declare the same thing.
/// </summary>
/// <docs>fundamentals/workers/instance-liveness</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/InstanceRegistrationLiveness.cs</code-under-test>
[Category("Unit")]
[Category("Shard5")]
public class InstanceRegistrationLivenessTests {
  [Test]
  public async Task WithTheHostsSourceAndCadence_TheModeAndTheWindowAreTheHeartbeatsAsync() {
    var options = new HeartbeatWorkerOptions { IntervalSeconds = 10, SlowIntervalSeconds = 20 };
    var services = new ServiceCollection()
      .AddSingleton<IInstanceConnectionModeSource>(new DirectMode())
      .AddSingleton(Options.Create(options))
      .BuildServiceProvider();

    var liveness = InstanceRegistrationLiveness.Of(services);

    await Assert.That(liveness).IsEqualTo(new InstanceRegistrationLiveness(
      InstanceConnectionMode.Direct, HeartbeatLivenessThreshold.StaleThresholdSeconds(options)));
  }

  [Test]
  public async Task WithNeitherRegistered_TheModeIsPooled_AndTheWindowIsTheDefaultCadencesAsync() {
    var liveness = InstanceRegistrationLiveness.Of(new ServiceCollection().BuildServiceProvider());

    await Assert.That(liveness).IsEqualTo(new InstanceRegistrationLiveness(
      InstanceConnectionMode.Pooled, HeartbeatLivenessThreshold.StaleThresholdSeconds(new HeartbeatWorkerOptions())))
      .Because("a host whose driver does not say registers pooled, as the heartbeat does");
  }

  [Test]
  public async Task WithNoServices_NothingIsDeclaredAsync() {
    await Assert.That(InstanceRegistrationLiveness.Of(null)).IsEqualTo(default(InstanceRegistrationLiveness))
      .Because("a registration with nothing to read keeps what the row records");
  }

  private sealed class DirectMode : IInstanceConnectionModeSource {
    public InstanceConnectionMode ConnectionMode => InstanceConnectionMode.Direct;
    public bool IsAliveLockHeld => true;
  }
}
