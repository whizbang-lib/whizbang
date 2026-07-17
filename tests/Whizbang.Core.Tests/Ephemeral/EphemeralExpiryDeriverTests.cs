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
/// Unit tests for the dispatch-time derivation of an AfterTtl event's absolute expiry from a payload.
/// The deriver mirrors <see cref="EphemeralFlagDeriver"/>: it consults the <see cref="IEphemeralModeResolver"/>
/// and, when the type declares a TTL (<c>TtlSeconds &gt;= 0</c>), returns <c>now + TtlSeconds</c> — the value
/// stamped onto <c>EnvelopeMetadata.EphemeralExpiresAt</c> so it rides the event into the body metadata.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
/// <tests>Whizbang.Core/Messaging/EphemeralExpiryDeriver.cs</tests>
[Category("Core")]
[Category("Ephemeral")]
public class EphemeralExpiryDeriverTests {
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

  private static readonly DateTimeOffset _now = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

  [Test]
  public async Task Derive_AfterTtl_ReturnsNowPlusTtlAsync() {
    var resolver = new StubResolver().Add(
      typeof(AfterTtlEvent), new EphemeralInfo(Destruction.AfterTtl, TransientStorage.TtlRow, RewindGraceSeconds: -1, TtlSeconds: 3600));
    var expiry = EphemeralExpiryDeriver.Derive(new AfterTtlEvent(), resolver, _now);
    await Assert.That(expiry).IsEqualTo(_now.AddSeconds(3600))
      .Because("An AfterTtl type's expiry is the dispatch instant plus its TtlSeconds.");
  }

  [Test]
  public async Task Derive_WhenConsumed_NoTtl_IsNullAsync() {
    var resolver = new StubResolver().Add(
      typeof(WhenConsumedEvent), new EphemeralInfo(Destruction.WhenConsumed, TransientStorage.PersistedRow));
    var expiry = EphemeralExpiryDeriver.Derive(new WhenConsumedEvent(), resolver, _now);
    await Assert.That(expiry).IsNull()
      .Because("A WhenConsumed event carries TtlSeconds = -1, so it has no age-based expiry.");
  }

  [Test]
  public async Task Derive_SourcedEvent_IsNullAsync() {
    var resolver = new StubResolver().Add(
      typeof(AfterTtlEvent), new EphemeralInfo(Destruction.AfterTtl, TransientStorage.TtlRow, RewindGraceSeconds: -1, TtlSeconds: 3600));
    var expiry = EphemeralExpiryDeriver.Derive(new SourcedEvent(), resolver, _now);
    await Assert.That(expiry).IsNull().Because("A Sourced (unknown) type has no expiry.");
  }

  [Test]
  public async Task Derive_NullPayloadOrResolver_IsNullAsync() {
    await Assert.That(EphemeralExpiryDeriver.Derive(null, new StubResolver(), _now)).IsNull();
    await Assert.That(EphemeralExpiryDeriver.Derive(new AfterTtlEvent(), resolver: null, _now)).IsNull();
  }
}
