// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Transports;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The topic integrity traffic addressed to this service travels on: the configured repair topic,
/// else the first topic the service consumes, else none (the callers then skip or refuse the send).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/RepairTopicResolver.cs</code-under-test>
public class RepairTopicResolverTests {
  [Test]
  public async Task Configured_WinsAsync() {
    var services = _services("first");

    await Assert.That(RepairTopicResolver.Resolve("repair", services)).IsEqualTo("repair");
  }

  [Test]
  public async Task Unset_FirstDestinationAsync() {
    var services = _services("first", "second");

    await Assert.That(RepairTopicResolver.Resolve(null, services)).IsEqualTo("first");
  }

  [Test]
  public async Task Unset_NoDestinations_NullAsync() {
    var services = _services();

    await Assert.That(RepairTopicResolver.Resolve(null, services)).IsNull();
  }

  [Test]
  public async Task Unset_NoConsumer_NullAsync() {
    var services = new ServiceCollection().BuildServiceProvider();

    await Assert.That(RepairTopicResolver.Resolve(null, services)).IsNull();
  }

  private static ServiceProvider _services(params string[] topics) {
    var options = new TransportConsumerOptions();
    foreach (var topic in topics) {
      options.Destinations.Add(new TransportDestination(topic));
    }
    return new ServiceCollection().AddSingleton(options).BuildServiceProvider();
  }
}
