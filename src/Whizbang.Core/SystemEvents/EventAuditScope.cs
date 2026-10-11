// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// The scope an <see cref="EventAudited"/> record carries: the audited event's tenant, user,
/// correlation id and claims, as one dictionary for generic access.
/// </summary>
/// <remarks>
/// Both writers of an event audit record (the auditing event-store decorator and the system event
/// emitter) build it from the same envelope facts, so they build it here, the same way.
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/EventAuditScopeTests.cs</tests>
internal static class EventAuditScope {
  /// <summary>The scope dictionary, or null when the event carries none of it.</summary>
  /// <param name="scopeContext">The audited envelope's current scope, if any.</param>
  /// <param name="correlationId">The audited envelope's correlation id, if any.</param>
  /// <returns>The non-empty scope, or null.</returns>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/EventAuditScopeTests.cs:Build_Everything_CarriesItAllAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/EventAuditScopeTests.cs:Build_Nothing_NullAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/SystemEvents/EventAuditScopeTests.cs:Build_ScopeWithoutIds_EmptyNullAsync</tests>
  internal static Dictionary<string, string?>? Build(IScopeContext? scopeContext, CorrelationId? correlationId) {
    var scope = new Dictionary<string, string?>();
    if (scopeContext?.Scope.TenantId is { } tenantId) {
      scope["TenantId"] = tenantId;
    }
    if (scopeContext?.Scope.UserId is { } userId) {
      scope["UserId"] = userId;
    }
    if (correlationId is { } correlation) {
      scope["CorrelationId"] = correlation.ToString();
    }
    if (scopeContext?.Claims is { } claims) {
      foreach (var claim in claims) {
        scope[claim.Key] = claim.Value;
      }
    }
    return scope.Count > 0 ? scope : null;
  }
}
