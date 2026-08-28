using Whizbang.Core.Observability;
using Whizbang.Core.Security;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Builds the single creation hop for control-plane traffic published directly to a transport.
/// </summary>
/// <remarks>
/// <para>
/// A handful of workers bypass <c>IDispatcher</c> and write their own envelope. Each still has to
/// stamp the same things, and stamping them by hand ten times is how they drifted apart in the
/// first place: the scope was simply left off, so intentionally-unscoped control-plane events were
/// stored identically to business events that had LOST their scope, and "this event has no scope"
/// could not be treated as a fault anywhere.
/// </para>
/// <para>
/// One factory keeps that decision in one place. It defers to
/// <see cref="SystemScopeResolver.ForUnscoped"/>, so a payload that is not control-plane — or is a
/// composite, whose scope becomes its children's — is left genuinely unscoped rather than marked.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/message-scope</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ControlPlaneHopTests.cs</tests>
public static class ControlPlaneHop {

  /// <summary>Creates the creation hop for a control-plane message.</summary>
  /// <param name="payloadType">The message's CLR type, used to decide the system marker.</param>
  /// <param name="instanceProvider">
  /// The publishing instance's provider, or null when none is registered. Resolving the fallback
  /// here rather than at each call site keeps ten copies of the same conditional from existing —
  /// copies are how the scope came to be omitted at every one of those sites to begin with.
  /// </param>
  /// <param name="timestamp">Hop timestamp.</param>
  /// <returns>A <see cref="HopType.Current"/> hop carrying the resolved scope.</returns>
  public static MessageHop Create(
      Type payloadType, IServiceInstanceProvider? instanceProvider, DateTimeOffset timestamp) {
    return new MessageHop {
      Type = HopType.Current,
      Timestamp = timestamp,
      ServiceInstance = instanceProvider?.ToInfo() ?? ServiceInstanceInfo.Unknown,
      Scope = SystemScopeResolver.ForUnscoped(payloadType),
    };
  }
}
