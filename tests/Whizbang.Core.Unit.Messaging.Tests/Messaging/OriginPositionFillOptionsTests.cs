// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2544: filling missing origin positions is on by default and bound from <c>Whizbang:StreamIntegrity:OriginPositionFill</c>;
/// a bound below its floor fails host start naming the key. Its request and answer are control-plane traffic.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/OriginPositions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Messaging/StreamIntegrityOptionsValidator.cs</code-under-test>
public class OriginPositionFillOptionsTests {
  [Test]
  public async Task OriginPositionFill_IsOnByDefault_WithTheDocumentedBoundsAsync() {
    var fill = new StreamIntegrityOptions().OriginPositionFill;

    await Assert.That(fill.Enabled).IsTrue().Because("turnkey: a service that configures nothing fills what it lacks");
    await Assert.That(fill.BatchSize).IsEqualTo(500);
    await Assert.That(fill.MaxBatchesPerCycle).IsEqualTo(10);
    await Assert.That(fill.LapPause).IsEqualTo(TimeSpan.FromMinutes(30));
  }

  [Test]
  public async Task OriginPositionFill_BindsFromItsSectionAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:StreamIntegrity:OriginPositionFill:Enabled"] = "false",
      ["Whizbang:StreamIntegrity:OriginPositionFill:BatchSize"] = "50",
      ["Whizbang:StreamIntegrity:OriginPositionFill:MaxBatchesPerCycle"] = "3",
      ["Whizbang:StreamIntegrity:OriginPositionFill:LapPause"] = "00:05:00",
    }).Build();
    var services = new ServiceCollection();
    services.TryAddWhizbangDefaults();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbangWorkers();
    await using var provider = services.BuildServiceProvider();

    var fill = provider.GetRequiredService<IOptions<StreamIntegrityOptions>>().Value.OriginPositionFill;

    await Assert.That(fill.Enabled).IsFalse();
    await Assert.That(fill.BatchSize).IsEqualTo(50);
    await Assert.That(fill.MaxBatchesPerCycle).IsEqualTo(3);
    await Assert.That(fill.LapPause).IsEqualTo(TimeSpan.FromMinutes(5));
  }

  [Test]
  public async Task OriginPositionFill_BoundsBelowTheirFloor_FailNamingTheKeyAsync() {
    var options = new StreamIntegrityOptions {
      OriginPositionFill = { BatchSize = 0, MaxBatchesPerCycle = 0, LapPause = TimeSpan.Zero },
    };

    var result = new StreamIntegrityOptionsValidator().Validate(null, options);

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains("Whizbang:StreamIntegrity:OriginPositionFill:BatchSize");
    await Assert.That(result.FailureMessage).Contains("Whizbang:StreamIntegrity:OriginPositionFill:MaxBatchesPerCycle");
    await Assert.That(result.FailureMessage).Contains("Whizbang:StreamIntegrity:OriginPositionFill:LapPause");
  }

  [Test]
  public async Task TheRequestAndItsAnswer_AreControlPlaneTrafficAsync() {
    await Assert.That(ControlPlaneTypeRegistry.IsControlPlane(TypeNameFormatter.Format(typeof(RequestOriginPositions)))).IsTrue();
    await Assert.That(ControlPlaneTypeRegistry.IsControlPlane(TypeNameFormatter.Format(typeof(OriginPositionsAnswer)))).IsTrue();
    var answer = new OriginPositionsAnswer { OriginServiceId = TrackedGuid.New().Value };
    await Assert.That(answer.Positions).IsEmpty();
    await Assert.That(answer.NotYetStamped).IsEmpty();
    await Assert.That(answer.Unknown).IsEmpty();
  }
}
