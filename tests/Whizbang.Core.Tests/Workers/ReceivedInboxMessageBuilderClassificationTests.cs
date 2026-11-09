// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A received message is classified as an event (and so copied into the event store) by the consumer registry's
/// event flag (#2064), not by the perspective event list: a receptor-only event in a service with perspectives, and any
/// consumed event in a service with no perspectives, used to read as a non-event and was never stored. The payload's
/// own marker decides only for a type the registry does not know.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/ReceivedInboxMessageBuilder.cs</code-under-test>
public class ReceivedInboxMessageBuilderClassificationTests {
  /// <summary>An event type.</summary>
  public sealed record ClassifiedEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>A command type.</summary>
  public sealed record ClassifiedCommand(Guid Id) : ICommand;

  /// <summary>An event the application declares but this service does not consume (an absorbed namespace's event).</summary>
  public sealed record CatalogedEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>A command the application declares but this service does not consume.</summary>
  public sealed record CatalogedCommand(Guid Id) : ICommand;

  private static readonly string _folded = TypeNameFormatter.Format(typeof(ClassifiedEvent));
  private const string RECEPTOR_ONLY = "Contracts.OrderShipped, Contracts";
  private const string CONSUMED_COMMAND = "Contracts.ShipOrder, Contracts";
  private const string UNKNOWN = "Contracts.NeverConsumed, Contracts";
  private static readonly JsonElement _json = JsonElement.Parse("{}");

  [Test]
  public async Task WithAPerspective_AReceptorOnlyEvent_IsAnEventAsync() {
    await using var provider = _services(new PerspectiveEvents(), _event(_folded), _event(RECEPTOR_ONLY));

    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, RECEPTOR_ONLY, _json)).IsTrue()
      .Because("the perspective folds another type, but this one is a consumed event, so it is stored");
  }

  [Test]
  public async Task WithNoPerspectives_AConsumedEvent_IsAnEventAsync() {
    await using var provider = _services(NullEventTypeProvider.Instance, _event(RECEPTOR_ONLY));

    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, RECEPTOR_ONLY, _json)).IsTrue()
      .Because("a transport payload arrives as JSON, so its marker cannot say; the consumer registry can");
  }

  [Test]
  public async Task AVersionQualifiedName_IsMatchedByItsTypeAndAssemblyAsync() {
    await using var provider = _services(NullEventTypeProvider.Instance, _event(RECEPTOR_ONLY));

    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(
      provider, RECEPTOR_ONLY + ", Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", _json)).IsTrue();
  }

  [Test]
  public async Task AConsumedCommand_IsNotAnEvent_WhateverItsPayloadAsync() {
    await using var provider = _services(NullEventTypeProvider.Instance, new ConsumedMessageInfo(CONSUMED_COMMAND, IsEvent: false));

    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, CONSUMED_COMMAND, new ClassifiedEvent(Guid.Empty)))
      .IsFalse().Because("the registry's flag is authoritative for a type it knows");
  }

  [Test]
  public async Task ATypeTheRegistryDoesNotKnow_FallsBackToThePayloadsMarkerAsync() {
    await using var provider = _services(NullEventTypeProvider.Instance, _event(RECEPTOR_ONLY));

    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, UNKNOWN, new ClassifiedEvent(Guid.Empty))).IsTrue();
    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, UNKNOWN, new ClassifiedCommand(Guid.Empty))).IsFalse();
    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, UNKNOWN, _json)).IsFalse();
  }

  [Test]
  public async Task WithNoRegistry_APerspectiveEvent_IsStillAnEventAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<IEventTypeProvider>(new PerspectiveEvents());
    await using var provider = services.BuildServiceProvider();

    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, _folded, _json)).IsTrue()
      .Because("a host without the consumer registry keeps the perspective list as a positive signal");
  }

  // ── #2074: a declared type the consumer registry does not know is classified by the message type catalog ──

  [Test]
  public async Task AnUnconsumedTypeTheCatalogDeclaresAnEvent_IsAnEvent_EvenAsJsonAsync() {
    await using var provider = _withCatalog(_entry(typeof(CatalogedEvent), "event"));

    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, TypeNameFormatter.Format(typeof(CatalogedEvent)), _json))
      .IsTrue().Because("an absorbed event arrives as JSON and nothing here consumes it; the catalog still knows it is an event");
  }

  [Test]
  public async Task AnUnconsumedTypeTheCatalogDeclaresACommand_IsNotAnEvent_WhateverItsPayloadAsync() {
    await using var provider = _withCatalog(_entry(typeof(CatalogedCommand), "command"));

    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(
      provider, TypeNameFormatter.Format(typeof(CatalogedCommand)), new ClassifiedEvent(Guid.Empty))).IsFalse()
      .Because("the catalog is authoritative for a type it declares");
  }

  [Test]
  public async Task AnUnconsumedPlainMessage_IsNotAnEventAsync() {
    await using var provider = _withCatalog(_entry(typeof(CatalogedEvent), "message"));

    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, TypeNameFormatter.Format(typeof(CatalogedEvent)), _json))
      .IsFalse().Because("a plain message is neither command nor event, and only an event is stored");
  }

  [Test]
  public async Task ATypeKnownByAFormerName_IsClassifiedByItsCatalogEntryAsync() {
    await using var provider = _withCatalog(_entry(typeof(CatalogedEvent), "event") with { FormerNames = ["Old.Contracts.Renamed"] });

    await Assert.That(ReceivedInboxMessageBuilder.ClassifiesAsEvent(provider, "Old.Contracts.Renamed, Old.Contracts", _json)).IsTrue()
      .Because("an envelope written under a pre-rename name is still the same declared event");
  }

  private static MessageTypeCatalogEntry _entry(Type type, string kind) =>
    new(type, TypeNameFormatter.FormatClrTypeName(type), kind, PinnedId: null);

  private static ServiceProvider _withCatalog(params MessageTypeCatalogEntry[] entries) {
    var services = new ServiceCollection();
    services.AddSingleton<IConsumerRegistry>(new FixedRegistry([]));
    services.AddSingleton<IMessageTypeCatalog>(new FixedCatalog(entries));
    services.TryAddWhizbangDefaults();
    return services.BuildServiceProvider();
  }

  private sealed class FixedCatalog(MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }

  private static ConsumedMessageInfo _event(string typeName) => new(typeName, IsEvent: true);

  private static ServiceProvider _services(IEventTypeProvider perspectives, params ConsumedMessageInfo[] consumed) {
    var services = new ServiceCollection();
    services.AddSingleton(perspectives);
    services.AddSingleton<IConsumerRegistry>(new FixedRegistry(consumed));
    return services.BuildServiceProvider();
  }

  /// <summary>The perspective folds <see cref="ClassifiedEvent"/> only.</summary>
  private sealed class PerspectiveEvents : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(ClassifiedEvent)];
  }

  private sealed class FixedRegistry(ConsumedMessageInfo[] consumed) : IConsumerRegistry {
    public bool HasAnyConsumer(string messageType) => consumed.Any(c => c.TypeName == messageType);
    public IReadOnlyList<ConsumedMessageInfo> GetConsumedMessages() => consumed;
  }
}
