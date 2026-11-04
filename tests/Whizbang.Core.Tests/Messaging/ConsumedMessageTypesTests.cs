// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Routing;

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

  [Test]
  public async Task AbsorbedNamespaces_AreTheRoutingOptionsAbsorbedNamespaces_LowercaseAndOrderedAsync() {
    var services = new ServiceCollection();
    services.AddSingleton(Options.Create(new RoutingOptions().AbsorbNamespaces("Contracts.Jobs", "contracts.archive")));
    await using var provider = services.BuildServiceProvider();

    await Assert.That(ConsumedMessageTypes.AbsorbedNamespaces(provider)).IsEquivalentTo(
      ["contracts.archive", "contracts.jobs"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task AbsorbedNamespaces_WithNoRoutingOptions_IsEmptyAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();

    await Assert.That(ConsumedMessageTypes.AbsorbedNamespaces(provider)).IsEmpty();
  }

  [Test]
  public async Task AbsorbedNamespaces_NullProvider_ThrowsAsync() {
    await Assert.That(() => ConsumedMessageTypes.AbsorbedNamespaces(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Keeps_AConsumedType_AndNotAnUnconsumedOneAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IConsumerRegistry>(new FixedRegistry(new ConsumedMessageInfo(CONSUMED, IsEvent: true)));
    await using var provider = services.BuildServiceProvider();

    var keeps = ConsumedMessageTypes.Keeps(provider);

    await Assert.That(keeps(CONSUMED)).IsTrue();
    await Assert.That(keeps(UNCONSUMED)).IsFalse()
      .Because("the receive boundary discards it, so nothing downstream may count it as this service's");
  }

  [Test]
  public async Task Keeps_WithNoRegistry_KeepsNothingAsync() {
    await using var provider = new ServiceCollection().BuildServiceProvider();

    await Assert.That(ConsumedMessageTypes.Keeps(provider)(CONSUMED)).IsFalse();
  }

  [Test]
  public async Task Keeps_ATypeARuntimeReceptorConsumesAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(new RuntimeRegistry(CONSUMED));
    await using var provider = services.BuildServiceProvider();

    await Assert.That(ConsumedMessageTypes.Keeps(provider)(CONSUMED)).IsTrue()
      .Because("a runtime-registered receptor keeps a message at the receive boundary too");
  }

  [Test]
  public async Task Keeps_AnUnconsumedTypeInAnAbsorbedNamespace_WhenTheDiscardPolicyKeepsItAsync() {
    var registry = new FixedRegistry();
    var services = new ServiceCollection();
    services.AddSingleton<IConsumerRegistry>(registry);
    services.AddSingleton<IMessageDiscardPolicy>(new MessageDiscardPolicy(
      registry, NullLogger<MessageDiscardPolicy>.Instance, new System.Diagnostics.Metrics.Meter("consumed-types-test"),
      Options.Create(new RoutingOptions().AbsorbNamespaces("contracts")),
      new EventMarkerResolver(NullMessageTypeCatalog.Instance)));
    await using var provider = services.BuildServiceProvider();

    await Assert.That(ConsumedMessageTypes.Keeps(provider)(UNCONSUMED)).IsTrue()
      .Because("an absorbed namespace's events are stored though nothing consumes them");
  }

  [Test]
  public async Task FindConsumedMessage_ByDefault_MatchesTheNameWithoutVersionDetailsAsync() {
    IConsumerRegistry registry = new FixedRegistry(new ConsumedMessageInfo(CONSUMED, IsEvent: true));

    await Assert.That(registry.FindConsumedMessage(CONSUMED + ", Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"))
      .IsEqualTo(new ConsumedMessageInfo(CONSUMED, IsEvent: true));
    await Assert.That(registry.FindConsumedMessage(UNCONSUMED)).IsNull();
  }

  private const string CONSUMED = "Contracts.OrderShipped, Contracts";
  private const string UNCONSUMED = "Contracts.KnownOnly, Contracts";

  private sealed class RuntimeRegistry(string consumedType) : IReceptorRegistry {
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool HasAnyRuntimeReceptors(string messageType) => messageType == consumedType;
  }

  private sealed class FixedRegistry(params ConsumedMessageInfo[] consumed) : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => consumed.Any(c => c.TypeName == messageType);
    public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() => consumed;
  }
}
