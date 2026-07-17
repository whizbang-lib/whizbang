using System;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Derives the absolute expiry instant for a <see cref="Attributes.Destruction.AfterTtl"/> ephemeral event
/// at dispatch time — the TTL analogue of <see cref="EphemeralFlagDeriver"/>. When the payload's resolved
/// <see cref="Attributes.EphemeralInfo"/> declares a TTL (<c>TtlSeconds &gt;= 0</c>), the expiry is
/// <c>now + TtlSeconds</c>; otherwise <c>null</c> (Sourced or WhenConsumed — no age-based expiry). The
/// result is stamped onto <c>EnvelopeMetadata.EphemeralExpiresAt</c> so it rides the event into
/// <c>wh_event_body.metadata</c>, where the reaper reads it as the AfterTtl reap floor.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
internal static class EphemeralExpiryDeriver {
  /// <summary>
  /// The absolute expiry (<paramref name="now"/> + the type's <c>TtlSeconds</c>) when
  /// <paramref name="payload"/> resolves to an ephemeral type that declares a TTL, otherwise <c>null</c>.
  /// The clock starts at dispatch/receive time (the plan's default TTL clock = fire-time). Null payload,
  /// null resolver, or an unknown/Sourced/WhenConsumed type all yield <c>null</c>.
  /// </summary>
  public static DateTimeOffset? Derive(object? payload, IEphemeralModeResolver? resolver, DateTimeOffset now) {
    if (payload is null || resolver is null) {
      return null;
    }
    var info = resolver.Resolve(payload.GetType());
    return info is not null && info.TtlSeconds >= 0
      ? now + TimeSpan.FromSeconds(info.TtlSeconds)
      : null;
  }
}
