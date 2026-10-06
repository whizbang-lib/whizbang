// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// Branch coverage for <see cref="ProcessWideOptionsBinding.BindCore"/> on a host that registers no
/// <c>IConfiguration</c> at all (a bare service collection, as a console tool or test host builds):
/// the banner key cannot be read, so the value the code set must stand.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Configuration/ProcessWideOptionsBinding.cs</code-under-test>
[Category("Configuration")]
public class ProcessWideOptionsBindingBranchCoverageTests {

  [Test]
  public async Task BindCore_NoConfigurationRegistered_KeepsTheCodeSetValuesAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();
    var options = new WhizbangCoreOptions {
      ShowBanner = false,
      EnableTagProcessing = false
    };

    var bound = ProcessWideOptionsBinding.BindCore(provider, options);

    await Assert.That(bound).IsSameReferenceAs(options);
    await Assert.That(bound.ShowBanner).IsFalse()
      .Because("with no configuration to read, the banner key is absent, and an absent key leaves the code's choice alone "
        + "rather than resetting it to the default of true");
    await Assert.That(bound.EnableTagProcessing).IsFalse()
      .Because("the Whizbang:Core section is empty without configuration, so nothing binds over the code");
  }
}
