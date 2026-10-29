// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>The flags a payload in hand declares through its marker interfaces.</summary>
/// <code-under-test>src/Whizbang.Core/Messaging/EventFlagsDeriver.cs</code-under-test>
public class EventFlagsDeriverTests {
  private sealed record Plain : IEvent;
  private sealed record Collective(CollectiveScope Scope, IReadOnlyList<Guid> MatchedStreamIds) : ICollectiveEvent;
  private sealed class Composite : ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => [];
  }

  [Whizbang.Core.Attributes.TerminalEvent]
  public sealed record Closed([property: StreamId] Guid StreamId = default) : IEvent;

  [Test]
  public async Task FromPayload_Plain_NoneAsync() =>
    await Assert.That(EventFlagsDeriver.FromPayload(new Plain(), null)).IsEqualTo(EventFlags.None);

  [Test]
  public async Task FromPayload_Composite_CompositeAsync() =>
    await Assert.That(EventFlagsDeriver.FromPayload(new Composite(), null)).IsEqualTo(EventFlags.Composite);

  [Test]
  public async Task FromPayload_Collective_CollectiveAsync() =>
    await Assert.That(EventFlagsDeriver.FromPayload(new Collective(null!, []), null)).IsEqualTo(EventFlags.Collective);

  [Test]
  public async Task FromPayload_ATerminalEvent_CarriesTheTerminalBitAsync() =>
    await Assert.That(EventFlagsDeriver.FromPayload(new Closed(), null)).IsEqualTo(EventFlags.Terminal)
      .Because("the generated catalog registers [TerminalEvent] types, and the dispatch path stamps the bit from that registration");

  [Test]
  public async Task FromPayload_NoPayload_NoneAsync() =>
    await Assert.That(EventFlagsDeriver.FromPayload(null, null)).IsEqualTo(EventFlags.None)
      .Because("a received message whose type the catalog does not know reaches the typed fallback with no payload in hand");

  [Test]
  public async Task Derive_ByWireName_ATerminalTypeTheCatalogKnows_CarriesTheTerminalBitAsync() {
    var resolver = new EventMarkerResolver(new TerminalCatalog());

    var flags = EventFlagsDeriver.Derive(payload: null, "Probe.Contracts.AccountClosed, Probe.Contracts", resolver, null);

    await Assert.That(flags).IsEqualTo(EventFlags.Terminal)
      .Because("a received payload is a JsonElement, so the receive path reads the terminal stamp from the catalog by name");
  }

  private sealed class TerminalCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [
      new(typeof(Closed), "Probe.Contracts.AccountClosed", "event", null) { IsTerminal = true },
    ];
  }
}
