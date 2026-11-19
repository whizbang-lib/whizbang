// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Branch backfill for <see cref="RoutingOptionsConfigurationBinder.Apply"/>: no configuration at
/// all, and a configuration without the routing section, apply nothing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/RoutingOptionsConfigurationBinder.cs</code-under-test>
public class RoutingOptionsConfigurationBinderBranchCoverageTests {

  [Test]
  public async Task Apply_NoConfiguration_LeavesTheCodeCallbacksDecisionAsync() {
    var options = new RoutingOptions();
    _ = options.RouteNoCommandNamespacesToInbox();

    RoutingOptionsConfigurationBinder.Apply(configuration: null, options);

    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse()
      .Because("with no configuration registered there is no flip set to apply; the code rollback stands");
    await Assert.That(options.CommandNamespacesToInbox).IsEmpty();
  }

  [Test]
  public async Task Apply_ConfigurationWithoutTheSection_LeavesTheCodeCallbacksDecisionAsync() {
    var options = new RoutingOptions();
    _ = options.RouteNoCommandNamespacesToInbox();
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Unrelated:Key"] = "value" })
      .Build();

    RoutingOptionsConfigurationBinder.Apply(configuration, options);

    await Assert.That(options.AllCommandNamespacesRouteToInbox).IsFalse()
      .Because("an absent routing section is a no-op");
  }
}
