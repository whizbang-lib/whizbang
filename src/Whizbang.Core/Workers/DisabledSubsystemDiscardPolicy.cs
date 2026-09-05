using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Decides whether an inbound message belongs to a subsystem the host has DISABLED and
/// should therefore be discarded as its processing (issue #664). Disabling a subsystem
/// stops its producers, but leftovers already in flight — or minted by an old build —
/// still arrive, find no active handler, never complete, and livelock on lease-expiry
/// re-claims. A discarded message completes through the normal machinery: the discard IS
/// the processing, so lifecycle completion is signaled by construction.
/// </summary>
/// <remarks>
/// Keyed on the INNER payload type (<see cref="EventTypeMatchingHelper.ExtractInnerPayloadTypeName"/>):
/// the envelope wrapper is a known, subscribed transport type, and judging it instead of
/// the payload is the wrapper-blindness that let these through. Pure string matching — an
/// old-build type name the current build cannot resolve still discards instead of
/// throwing. An unreadable name fails SAFE: discard requires a positive match.
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/DisabledSubsystemDiscardTests.cs</tests>
public static class DisabledSubsystemDiscardPolicy {

  private static readonly string _checkpoint = EventTypeMatchingHelper.NormalizeTypeName(TypeNameFormatter.Format(typeof(IntegrityCheckpoint)));
  private static readonly string _gapDetected = EventTypeMatchingHelper.NormalizeTypeName(TypeNameFormatter.Format(typeof(IntegrityGapDetected)));
  private static readonly string _manifest = EventTypeMatchingHelper.NormalizeTypeName(TypeNameFormatter.Format(typeof(IntegrityManifest)));
  private static readonly string _manifestRequest = EventTypeMatchingHelper.NormalizeTypeName(TypeNameFormatter.Format(typeof(RequestIntegrityManifest)));
  private static readonly string _divergence = EventTypeMatchingHelper.NormalizeTypeName(TypeNameFormatter.Format(typeof(IntegrityDivergenceDetected)));

  /// <summary>
  /// True when the message's inner payload type belongs to a subsystem
  /// <paramref name="options"/> has disabled — the caller discards it by completing it.
  /// </summary>
  public static bool ShouldDiscard(string messageTypeName, StreamIntegrityOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrEmpty(messageTypeName)) {
      return false;
    }
    var inner = EventTypeMatchingHelper.NormalizeTypeName(
      EventTypeMatchingHelper.ExtractInnerPayloadTypeName(messageTypeName));
    if (!options.CheckpointsEnabled && string.Equals(inner, _checkpoint, StringComparison.Ordinal)) {
      return true;
    }
    if (!options.GapDetectionEnabled && string.Equals(inner, _gapDetected, StringComparison.Ordinal)) {
      return true;
    }
    if (!options.AuditEnabled
        && (string.Equals(inner, _manifest, StringComparison.Ordinal)
          || string.Equals(inner, _manifestRequest, StringComparison.Ordinal)
          || string.Equals(inner, _divergence, StringComparison.Ordinal))) {
      return true;
    }
    return false;
  }
}
