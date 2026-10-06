// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Branch backfill for <see cref="RoutingBuilderExtensions.WithRouting"/>'s control-class
/// adoption: the DI-bound instance is adopted when one is registered, and a registration that
/// yields no instance leaves the routing options' own control-class settings in place.
/// </summary>
public class RoutingBuilderExtensionsBranchCoverageTests {

  [Test]
  public async Task WithRouting_ControlClassAccessorYieldsNoInstance_KeepsTheCodeConfiguredControlClassAsync() {
    var services = new ServiceCollection();
    new WhizbangBuilder(services).WithRouting(r => r.ControlClass.CadenceMultiplier = 11);
    services.AddSingleton<IOptions<ControlClassOptions>>(new NullValueOptions());
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.ControlClass).IsNotNull()
      .Because("an accessor with no value must not null out the routing options' control class");
    await Assert.That(options.ControlClass.CadenceMultiplier).IsEqualTo(11)
      .Because("with nothing to adopt, the code callback's control-class settings stand");
  }

  [Test]
  public async Task WithRouting_ControlClassRegistered_AdoptsTheDiBoundInstanceAsync() {
    var services = new ServiceCollection();
    new WhizbangBuilder(services).WithRouting(r => r.ControlClass.CadenceMultiplier = 11);
    var bound = new ControlClassOptions { CadenceMultiplier = 3 };
    services.AddSingleton<IOptions<ControlClassOptions>>(Options.Create(bound));
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.ControlClass).IsSameReferenceAs(bound)
      .Because("the strategy, the mint and the receive boundary must all read one control-class object");
  }

  private sealed class NullValueOptions : IOptions<ControlClassOptions> {
    public ControlClassOptions Value => null!;
  }
}
