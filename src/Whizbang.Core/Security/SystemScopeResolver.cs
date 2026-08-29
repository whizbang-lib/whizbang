using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;

namespace Whizbang.Core.Security;

/// <summary>
/// Decides whether a message that resolved NO scope should be marked system-originated.
/// </summary>
/// <remarks>
/// <para>
/// Control-plane traffic — checkpoints, manifests, gap signals — is published by background workers
/// with no ambient user, by design. Storing that as a null scope makes it identical to a business
/// event whose scope was dropped, and while the two are indistinguishable, "this event has no
/// scope" cannot be asserted as a fault. It has to be investigated instead, every time.
/// </para>
/// <para>
/// Marking the intentional case is what makes the accidental case detectable. It states intent, not
/// permission: <see cref="ScopeDelta.System"/> resolves to no tenant, no user, no principal.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/message-scope</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/SystemScopeSentinelTests.cs</tests>
public static class SystemScopeResolver {

  /// <summary>
  /// The scope to stamp on a message that resolved none, or null to leave it genuinely unscoped.
  /// </summary>
  /// <param name="messageType">The message's CLR type, or null when it is not known.</param>
  /// <returns><see cref="ScopeDelta.System"/> for control-plane traffic; otherwise null.</returns>
  public static ScopeDelta? ForUnscoped(Type? messageType) => ForUnscoped(messageType, declaredUnscopedTypes: null);

  /// <summary>
  /// The scope to stamp on a message that resolved none, honoring the caller's declared exemptions.
  /// </summary>
  /// <param name="messageType">The message's CLR type, or null when it is not known.</param>
  /// <param name="declaredUnscopedTypes">
  /// Types the application author declared as carrying no authority — in practice
  /// <c>MessageSecurityOptions.ExemptMessageTypes</c>. These get their own marker rather than the
  /// framework's, so an auditor can tell an author's assertion from the framework's own traffic.
  /// </param>
  /// <returns>The marker to stamp, or null to leave the message genuinely unscoped.</returns>
  public static ScopeDelta? ForUnscoped(Type? messageType, IReadOnlySet<Type>? declaredUnscopedTypes) {
    if (messageType is null) {
      // An unknown type is not evidence of intent. Guessing would mark real traffic as system and
      // exempt it from the invariant.
      return null;
    }

    // A composite is excluded even when it is itself control-plane. Its hop scope becomes its
    // CHILDREN's scope at fan-out, and those children are ordinary domain events — a wrapper must
    // never launder a system marker onto the traffic it carries. That is the same coupling that
    // let a scopeless repair bundle persist a seven-figure population of unscoped business events.
    if (typeof(CompositeEventBase).IsAssignableFrom(messageType)
        || typeof(ICompositeEvent).IsAssignableFrom(messageType)) {
      return null;
    }

    // Control-plane wins over any declaration. Framework traffic is framework traffic whether or
    // not a consumer also listed the type, and the provenance an auditor sees must not depend on
    // one service's configuration.
    if (typeof(IControlPlaneMessage).IsAssignableFrom(messageType)) {
      return ScopeDelta.System;
    }

    // An event the author declared unscoped is intentional, but it is THEIR assertion, not the
    // framework's — so it carries its own marker. Anything else stays blank, which is what keeps a
    // dropped scope detectable.
    return declaredUnscopedTypes?.Contains(messageType) == true
      ? ScopeDelta.DeclaredUnscoped
      : null;
  }
}
