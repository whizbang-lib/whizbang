// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Routing.ExcludedEventFixtures;

#pragma warning disable CA1707 // Test method naming uses underscores by convention

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// The opt-out list of event types (#2175): the registration API and the bound configuration key
/// <c>Whizbang:Routing:ExcludedEventTypes</c>, merged.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Routing/RoutingOptions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Routing/RoutingOptionsConfigurationBinder.cs</code-under-test>
public class RoutingOptionsExcludedEventTypesTests {
  [Test]
  public async Task ExcludedEventTypes_ByDefault_IsEmptyAsync() {
    await Assert.That(new RoutingOptions().ExcludedEventTypes).IsEmpty();
  }

  [Test]
  public async Task ExcludeEventType_Generic_RecordsTheWireFormAsync() {
    var options = new RoutingOptions().ExcludeEventType<NoisyEvent>();

    await Assert.That(options.ExcludedEventTypes).IsEquivalentTo([TypeNameFormatter.Format(typeof(NoisyEvent))]);
  }

  [Test]
  public async Task ExcludeEventType_NestedType_RecordsTheWireFormAsync() {
    // The Type overload on purpose: the form a host uses when the type is only known at startup.
    var eventType = typeof(Container.NestedNoisyEvent);
    var options = new RoutingOptions().ExcludeEventType(eventType);

    await Assert.That(options.ExcludedEventTypes)
      .IsEquivalentTo(["Whizbang.Core.Tests.Routing.ExcludedEventFixtures.Container+NestedNoisyEvent, Whizbang.Core.Tests"]);
  }

  [Test]
  public async Task ExcludeEventType_NullType_ThrowsAsync() {
    await Assert.That(() => new RoutingOptions().ExcludeEventType(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ExcludeEventTypes_Names_StripVersionAndSkipBlankAsync() {
    var options = new RoutingOptions().ExcludeEventTypes(
      "MyApp.Events.Noisy, MyApp, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null",
      "  ",
      "MyApp.Events.Outer+Chatty");

    await Assert.That(options.ExcludedEventTypes)
      .IsEquivalentTo(["MyApp.Events.Noisy, MyApp", "MyApp.Events.Outer+Chatty"]);
  }

  [Test]
  public async Task ExcludeEventTypes_NullArray_ThrowsAsync() {
    await Assert.That(() => new RoutingOptions().ExcludeEventTypes(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task ExcludeEventTypes_ReturnsTheSameOptionsForChainingAsync() {
    var options = new RoutingOptions();

    await Assert.That(options.ExcludeEventTypes("MyApp.Events.Noisy")).IsSameReferenceAs(options);
    await Assert.That(options.ExcludeEventType<NoisyEvent>()).IsSameReferenceAs(options);
  }

  [Test]
  public async Task WithRouting_ConfigurationList_IsMergedWithTheCodeListAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:ExcludedEventTypes:0"] = "MyApp.Events.FromConfiguration, MyApp",
        ["Whizbang:Routing:ExcludedEventTypes:1"] = " ",
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbang().WithRouting(routing => routing.ExcludeEventType<NoisyEvent>());

    var options = services.BuildServiceProvider().GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.ExcludedEventTypes).IsEquivalentTo([
      TypeNameFormatter.Format(typeof(NoisyEvent)),
      "MyApp.Events.FromConfiguration, MyApp"]);
  }

  [Test]
  public async Task WithRouting_ConfigurationSectionWithoutTheList_LeavesTheCodeListAsync() {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?> {
        ["Whizbang:Routing:RetireSharedInbox"] = "true",
      })
      .Build();
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(configuration);
    services.AddWhizbang().WithRouting(routing => routing.ExcludeEventType<NoisyEvent>());

    var options = services.BuildServiceProvider().GetRequiredService<IOptions<RoutingOptions>>().Value;

    await Assert.That(options.ExcludedEventTypes).IsEquivalentTo([TypeNameFormatter.Format(typeof(NoisyEvent))]);
  }
}
