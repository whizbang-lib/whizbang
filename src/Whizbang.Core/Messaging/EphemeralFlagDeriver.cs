using System;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Derives the <see cref="EventFlags.Ephemeral"/> category flag for a payload at dispatch time — the
/// ephemeral analogue of the inline <c>payload is ICompositeEvent</c> / <c>ICollectiveEvent</c> checks.
/// Ephemeral is composable (the <c>[Ephemeral]</c> authority can sit on a base record or a marker interface,
/// not just <see cref="IEphemeralEvent"/>), so this consults the source-generated
/// <see cref="IEphemeralModeResolver"/> as well, and falls back to the shipped <see cref="IEphemeralEvent"/>
/// marker when no resolver is wired (some minimal test hosts). The resulting flag is persisted on
/// <c>wh_event_store.flags</c> and read by the emit chain (body offload) and the reaper.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
internal static class EphemeralFlagDeriver {
  /// <summary>
  /// <see cref="EventFlags.Ephemeral"/> when <paramref name="payload"/> is ephemeral (the
  /// <see cref="IEphemeralEvent"/> marker, or the <paramref name="resolver"/> recognises its runtime type),
  /// otherwise <see cref="EventFlags.None"/>. Null payload / null resolver are handled (safe default None).
  /// </summary>
  public static EventFlags Derive(object? payload, IEphemeralModeResolver? resolver) {
    if (payload is null) {
      return EventFlags.None;
    }
    if (payload is IEphemeralEvent) {
      return EventFlags.Ephemeral;
    }
    return resolver?.IsEphemeral(payload.GetType()) == true
      ? EventFlags.Ephemeral
      : EventFlags.None;
  }
}
