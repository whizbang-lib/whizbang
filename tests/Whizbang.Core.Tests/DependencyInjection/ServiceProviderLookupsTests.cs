// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.DependencyInjection;

namespace Whizbang.Core.Tests.DependencyInjection;

/// <summary>
/// A logger and a bound options value, read when the host registered them and done without when it
/// did not.
/// </summary>
/// <code-under-test>src/Whizbang.Core/DependencyInjection/ServiceProviderLookups.cs</code-under-test>
public class ServiceProviderLookupsTests {
  [Test]
  public async Task Logger_Registered_IsTheHostsAsync() {
    using var services = new ServiceCollection().AddLogging().BuildServiceProvider();

    var logger = services.GetLoggerOrNullLogger<ServiceProviderLookupsTests>();

    await Assert.That(logger).IsSameReferenceAs(services.GetRequiredService<ILogger<ServiceProviderLookupsTests>>());
  }

  [Test]
  public async Task Logger_Unregistered_DiscardsAsync() {
    using var services = new ServiceCollection().BuildServiceProvider();

    await Assert.That(services.GetLoggerOrNullLogger<ServiceProviderLookupsTests>())
      .IsSameReferenceAs(NullLogger<ServiceProviderLookupsTests>.Instance);
  }

  [Test]
  public async Task Options_Registered_IsTheValueAsync() {
    using var services = new ServiceCollection().Configure<Probe>(p => p.Value = 7).BuildServiceProvider();

    await Assert.That(services.GetOptionsValue<Probe>()!.Value).IsEqualTo(7);
  }

  [Test]
  public async Task Options_Unregistered_NullAsync() {
    using var services = new ServiceCollection().BuildServiceProvider();

    await Assert.That(services.GetOptionsValue<Probe>()).IsNull();
  }

  private sealed class Probe {
    public int Value { get; set; }
  }
}
