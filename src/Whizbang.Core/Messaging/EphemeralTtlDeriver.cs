namespace Whizbang.Core.Messaging;

/// <summary>
/// Derives the TTL (seconds) for a <see cref="Attributes.Destruction.AfterTtl"/> ephemeral event at dispatch
/// time — the TTL analogue of <see cref="EphemeralFlagDeriver"/>. Returns the type's declared
/// <c>TtlSeconds</c> (when <c>&gt;= 0</c>), otherwise <c>null</c> (Sourced or WhenConsumed — no age-based
/// expiry). The value is stamped onto <c>EnvelopeMetadata.EphemeralTtlSeconds</c> so it rides the event into
/// <c>wh_event_body.metadata</c>; the emit chain then materialises the absolute expiry as
/// <c>created_at + ttl</c> — i.e. anchored to the event's authoritative DB creation timestamp and DB clock,
/// NOT the C# dispatch moment.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
internal static class EphemeralTtlDeriver {
  /// <summary>
  /// The type's <c>TtlSeconds</c> when <paramref name="payload"/> resolves to an ephemeral type that declares
  /// a TTL, otherwise <c>null</c>. Null payload, null resolver, or an unknown/Sourced/WhenConsumed type all
  /// yield <c>null</c>. The clock is deliberately NOT applied here — the expiry is anchored to the event's
  /// <c>created_at</c> at emit time, so this returns the duration, not an instant.
  /// </summary>
  public static int? Derive(object? payload, IEphemeralModeResolver? resolver) {
    if (payload is null || resolver is null) {
      return null;
    }
    var info = resolver.Resolve(payload.GetType());
    return info is not null && info.TtlSeconds >= 0 ? info.TtlSeconds : null;
  }
}
