using Whizbang.Core.Observability;

namespace Whizbang.Core.Security;

/// <summary>
/// Resolves the scope stamped on the hop of an event on its way to the outbox.
/// </summary>
/// <remarks>
/// <para>
/// Hop-first, then ambient — the established precedence — and only then the marker that records
/// why an event legitimately carries no scope at all.
/// </para>
/// <para>
/// This exists because the publish path does not share the envelope builder that Send and
/// LocalInvoke use, so wiring the marker there covered every path except the one that matters most:
/// control-plane events are PUBLISHED. A deployment running the marker build still wrote coverage-gap
/// events with a null scope and no scope on the hop, and the missing-scope invariant reported them
/// as defects. Keeping the resolution in one place is what stops the two paths drifting again.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/message-security#scope-markers</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/SystemScopeSentinelTests.cs</tests>
public static class OutboxHopScope {

  /// <summary>Resolves the hop scope for an event being written to the outbox.</summary>
  /// <param name="sourceEnvelope">The envelope this emit descends from, or null at a root emit.</param>
  /// <param name="payloadType">The event's CLR type, used to decide a marker.</param>
  /// <param name="declaredUnscopedTypes">Types the author declared as carrying no authority.</param>
  /// <returns>The scope delta for the hop, or null when the event is genuinely unscoped.</returns>
  public static ScopeDelta? Resolve(
      IMessageEnvelope? sourceEnvelope,
      Type? payloadType,
      IReadOnlySet<Type>? declaredUnscopedTypes) {
    // A real scope always wins: a marker must never displace actual authority.
    return CascadeContext.ResolveHopFirstScope(sourceEnvelope)
      ?? SystemScopeResolver.ForUnscoped(payloadType, declaredUnscopedTypes);
  }
}
