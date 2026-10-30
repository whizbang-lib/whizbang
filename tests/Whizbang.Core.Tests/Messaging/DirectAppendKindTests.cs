// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// <see cref="DirectAppendKind"/>: the kind bits a direct append stores with an event, the ones the dispatch path
/// stamps on the same payload, and nothing else (#1546).
/// </summary>
/// <docs>operations/diagnostics/whiz133</docs>
public class DirectAppendKindTests {
  public sealed record Typing([property: StreamId] Guid Id) : IEphemeralEvent;
  public sealed record Noted([property: StreamId] Guid Id) : IEvent;
  public sealed record Declared([property: StreamId] Guid Id) : IEvent;
  [TerminalEvent]
  public sealed record Closed([property: StreamId] Guid Id) : IEvent;
  public sealed record Bundle : Whizbang.Core.Minting.ICompositeEvent {
    public IEnumerable<IMessage> InnerEvents => [];
  }

  [Test]
  public async Task Of_AnEventMarkedEphemeral_IsTheEphemeralBitAsync() =>
    await Assert.That(DirectAppendKind.Of(new Typing(Guid.Empty), null)).IsEqualTo(8);

  [Test]
  public async Task Of_ACompactionOrigin_IsTheCompactedBitAsync() {
    var origin = new Compacted {
      StreamId = Guid.Empty,
      PerspectiveName = "Probe",
      Model = JsonSerializer.SerializeToElement(1),
    };

    await Assert.That(DirectAppendKind.Of(origin, null)).IsEqualTo(16);
  }

  [Test]
  public async Task Of_ASourcedEvent_IsZeroAsync() =>
    await Assert.That(DirectAppendKind.Of(new Noted(Guid.Empty), null)).IsEqualTo(0);

  [Test]
  public async Task Of_AnEventTheResolverDeclaresEphemeral_IsTheEphemeralBitAsync() =>
    await Assert.That(DirectAppendKind.Of(new Declared(Guid.Empty), new DeclaresEphemeral(typeof(Declared)))).IsEqualTo(8);

  [Test]
  public async Task Of_ACompositeEvent_KeepsNoRoutingBitAsync() =>
    await Assert.That(DirectAppendKind.Of(new Bundle(), null)).IsEqualTo(0)
      .Because("a direct append creates no routing work, so the row must not claim it");

  [Test]
  public async Task Of_ATerminalEvent_KeepsTheTerminalBitAsync() =>
    await Assert.That(DirectAppendKind.Of(new Closed(Guid.Empty), null)).IsEqualTo(64)
      .Because("wh_append_event seals the stream when the event it stores carries the terminal bit (WS-3 S4)");

  private sealed class DeclaresEphemeral(Type ephemeralType) : IEphemeralModeResolver {
    public EphemeralInfo? Resolve(string clrTypeName) => null;
    public EphemeralInfo? Resolve(Type type) => null;
    public bool IsEphemeral(string clrTypeName) => false;
    public bool IsEphemeral(Type type) => type == ephemeralType;
  }
}
