// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;

namespace Whizbang.Core.Security;

/// <summary>
/// Builds the scope a message carries on its hop: the one place every dispatch path (send, publish, the
/// outbox, batches, scheduled and deferred messages, local runs, cascades, the transport bridges and the
/// event-store decorator) turns a scope into the <see cref="ScopeDelta"/> stamped on the wire.
/// </summary>
/// <docs>fundamentals/security/message-security#hop-scope</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/HopScopeTests.cs</tests>
public static class HopScope {

  /// <summary>
  /// The hop scope of the ambient scope context, or <see langword="null"/> when there is none to propagate.
  /// </summary>
  /// <returns>The hop scope, or null.</returns>
  public static ScopeDelta? FromAmbient() =>
    ScopeDelta.FromSecurityContext(CascadeContext.GetSecurityFromAmbient());

  /// <summary>
  /// The hop scope of an explicit message context, or <see langword="null"/> when it carries no tenant or user.
  /// </summary>
  /// <param name="context">The message context the message is dispatched under.</param>
  /// <returns>The hop scope, or null.</returns>
  public static ScopeDelta? FromMessageContext(IMessageContext context) {
    ArgumentNullException.ThrowIfNull(context);
    return ScopeDelta.FromSecurityContext(CascadeContext.SecurityFromIds(context.TenantId, context.UserId));
  }

  /// <summary>
  /// The hop scope carried by a source envelope, or <see langword="null"/> when it carries no tenant or user.
  /// </summary>
  /// <param name="envelope">The envelope the message descends from, or null.</param>
  /// <returns>The hop scope, or null.</returns>
  public static ScopeDelta? FromEnvelope(IMessageEnvelope? envelope) =>
    envelope?.GetCurrentScope() is { } scope
      ? ScopeDelta.FromSecurityContext(CascadeContext.SecurityFromScope(scope.Scope))
      : null;
}
