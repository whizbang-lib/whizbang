// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Branch backfill for <see cref="ControlClassOptionsConfigurationBinder"/>: a host with no
/// configuration registered, and one whose configuration has no control-class section, leave
/// the code-configured options untouched.
/// </summary>
public class ControlClassOptionsConfigurationBinderBranchCoverageTests {

  private static ControlClassOptions _codeConfigured() => new() {
    Enabled = false,
    CadenceMultiplier = 7,
    SessionlessSubscriptions = true,
  };

  [Test]
  public async Task PostConfigure_NoConfiguration_LeavesOptionsUntouchedAsync() {
    var options = _codeConfigured();

    new ControlClassOptionsConfigurationBinder(configuration: null).PostConfigure(null, options);

    await Assert.That(options.Enabled).IsFalse()
      .Because("with no configuration registered there is nothing to bind; the code callback's values stand");
    await Assert.That(options.CadenceMultiplier).IsEqualTo(7);
    await Assert.That(options.SessionlessSubscriptions).IsTrue();
  }

  [Test]
  public async Task PostConfigure_ConfigurationWithoutTheSection_LeavesOptionsUntouchedAsync() {
    var options = _codeConfigured();
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Unrelated:Key"] = "value" })
      .Build();

    new ControlClassOptionsConfigurationBinder(configuration).PostConfigure(null, options);

    await Assert.That(options.Enabled).IsFalse()
      .Because("an absent section is a no-op, not a reset to defaults");
    await Assert.That(options.CadenceMultiplier).IsEqualTo(7);
  }
}
