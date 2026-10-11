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

  [Test]
  public async Task FromPayload_Plain_NoneAsync() =>
    await Assert.That(EventFlagsDeriver.FromPayload(new Plain(), null)).IsEqualTo(EventFlags.None);

  [Test]
  public async Task FromPayload_Composite_CompositeAsync() =>
    await Assert.That(EventFlagsDeriver.FromPayload(new Composite(), null)).IsEqualTo(EventFlags.Composite);

  [Test]
  public async Task FromPayload_Collective_CollectiveAsync() =>
    await Assert.That(EventFlagsDeriver.FromPayload(new Collective(null!, []), null)).IsEqualTo(EventFlags.Collective);
}
