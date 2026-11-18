// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Branch backfill for <see cref="PoisonMessageOptionsConfigurationBinder"/>: a host with no
/// configuration registered, and one whose configuration has no poison-detection section, leave
/// the code-configured options untouched.
/// </summary>
public class PoisonMessageOptionsConfigurationBinderBranchCoverageTests {

  private static PoisonMessageOptions _codeConfigured() => new() {
    Enabled = false,
    MaxDeliveryAttempts = 9,
  };

  [Test]
  public async Task PostConfigure_NoConfiguration_LeavesOptionsUntouchedAsync() {
    var options = _codeConfigured();

    new PoisonMessageOptionsConfigurationBinder(configuration: null).PostConfigure(null, options);

    await Assert.That(options.Enabled).IsFalse()
      .Because("with no configuration registered there is nothing to bind; the code callback's values stand");
    await Assert.That(options.MaxDeliveryAttempts).IsEqualTo(9);
  }

  [Test]
  public async Task PostConfigure_ConfigurationWithoutTheSection_LeavesOptionsUntouchedAsync() {
    var options = _codeConfigured();
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> { ["Unrelated:Key"] = "value" })
      .Build();

    new PoisonMessageOptionsConfigurationBinder(configuration).PostConfigure(null, options);

    await Assert.That(options.Enabled).IsFalse()
      .Because("an absent section is a no-op, not a reset to defaults");
    await Assert.That(options.MaxDeliveryAttempts).IsEqualTo(9);
  }
}
