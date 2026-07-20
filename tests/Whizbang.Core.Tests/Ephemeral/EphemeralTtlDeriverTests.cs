using System;
using System.Collections.Generic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Tests.Ephemeral;

/// <summary>
/// Unit tests for the dispatch-time derivation of an AfterTtl event's TTL from a payload. The deriver mirrors
/// <see cref="EphemeralFlagDeriver"/>: it consults the <see cref="IEphemeralModeResolver"/> and, when the type
/// declares a TTL (<c>TtlSeconds &gt;= 0</c>), returns that duration — NOT an instant. The absolute expiry is
/// materialised later as <c>created_at + ttl</c> at emit time (DB-clock-authoritative), so no clock is applied
/// here.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
/// <tests>Whizbang.Core/Messaging/EphemeralTtlDeriver.cs</tests>
[Category("Core")]
[Category("Ephemeral")]
public class EphemeralTtlDeriverTests {
  private sealed record AfterTtlEvent;
  private sealed record WhenConsumedEvent;
  private sealed record SourcedEvent;

  private sealed class StubResolver : IEphemeralModeResolver {
    private readonly Dictionary<Type, EphemeralInfo> _byType = [];
    public StubResolver Add(Type type, EphemeralInfo info) { _byType[type] = info; return this; }
    public EphemeralInfo? Resolve(string clrTypeName) => null;
    public bool IsEphemeral(string clrTypeName) => false;
    public EphemeralInfo? Resolve(Type type) => _byType.TryGetValue(type, out var i) ? i : null;
    public bool IsEphemeral(Type type) => _byType.ContainsKey(type);
  }

  [Test]
  public async Task Derive_AfterTtl_ReturnsTtlSecondsAsync() {
    var resolver = new StubResolver().Add(
      typeof(AfterTtlEvent), new EphemeralInfo(Destruction.AfterTtl, TransientStorage.TtlRow, RewindGraceSeconds: -1, TtlSeconds: 3600));
    await Assert.That(EphemeralTtlDeriver.Derive(new AfterTtlEvent(), resolver)).IsEqualTo(3600)
      .Because("An AfterTtl type's TTL (the duration) is carried on the event; the expiry is created_at + ttl at emit.");
  }

  [Test]
  public async Task Derive_WhenConsumed_NoTtl_IsNullAsync() {
    var resolver = new StubResolver().Add(
      typeof(WhenConsumedEvent), new EphemeralInfo(Destruction.WhenConsumed, TransientStorage.PersistedRow));
    await Assert.That(EphemeralTtlDeriver.Derive(new WhenConsumedEvent(), resolver)).IsNull()
      .Because("A WhenConsumed event carries TtlSeconds = -1, so it has no age-based expiry.");
  }

  [Test]
  public async Task Derive_SourcedEvent_IsNullAsync() {
    var resolver = new StubResolver().Add(
      typeof(AfterTtlEvent), new EphemeralInfo(Destruction.AfterTtl, TransientStorage.TtlRow, RewindGraceSeconds: -1, TtlSeconds: 3600));
    await Assert.That(EphemeralTtlDeriver.Derive(new SourcedEvent(), resolver)).IsNull()
      .Because("A Sourced (unknown) type has no TTL.");
  }

  [Test]
  public async Task Derive_NullPayloadOrResolver_IsNullAsync() {
    await Assert.That(EphemeralTtlDeriver.Derive(null, new StubResolver())).IsNull();
    await Assert.That(EphemeralTtlDeriver.Derive(new AfterTtlEvent(), resolver: null)).IsNull();
  }
}
