// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// The consumed event types, read from the one registry that answers "what does this service consume" (#2058).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/ConsumedMessageTypes.cs</code-under-test>
public class ConsumedMessageTypesTests {

  [Test]
  public async Task EventTypeNames_AreTheConsumedEvents_DeduplicatedInOrdinalOrder_WithoutCommandsAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IConsumerRegistry>(new FixedRegistry(
      new ConsumedMessageInfo("MyApp.Zeta, MyApp", IsEvent: true),
      new ConsumedMessageInfo("MyApp.Command, MyApp", IsEvent: false),
      new ConsumedMessageInfo("MyApp.Alpha, MyApp", IsEvent: true),
      new ConsumedMessageInfo("MyApp.Alpha, MyApp", IsEvent: true)));
    await using var provider = services.BuildServiceProvider();

    var names = ConsumedMessageTypes.EventTypeNames(provider);

    await Assert.That(names).IsEquivalentTo(["MyApp.Alpha, MyApp", "MyApp.Zeta, MyApp"],
      TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("only events are audited, each once, in a deterministic order");
  }

  [Test]
  public async Task EventTypeNames_WithNoRegistry_IsEmptyAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();

    await Assert.That(ConsumedMessageTypes.EventTypeNames(provider)).IsEmpty()
      .Because("with nothing to say what is consumed, nothing is; never 'everything'");
  }

  [Test]
  public async Task EventTypeNames_NullProvider_ThrowsAsync() {
    await Assert.That(() => ConsumedMessageTypes.EventTypeNames(null!)).Throws<ArgumentNullException>();
  }

  private sealed class FixedRegistry(params ConsumedMessageInfo[] consumed) : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => consumed.Any(c => c.TypeName == messageType);
    public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() => consumed;
  }
}
