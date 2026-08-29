using Whizbang.Core.Lenses;
using Whizbang.Core.Security;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// The scope stamped on an audit record's own envelope.
/// </summary>
/// <remarks>
/// <para>
/// The scope column is an ACCESS-CONTROL key — reads filter on <c>scope-&gt;&gt;'t'</c> and
/// <c>scope-&gt;&gt;'u'</c> — so what goes in it decides who can reach the row, not merely how it is
/// labelled. That splits the audited event's identity in two.
/// </para>
/// <list type="bullet">
///   <item>
///     The audited TENANT is carried, so tenant-scoped reads, exports and deletions reach the row.
///     Audit records hold personal data; leaving them outside every tenant partition is a retention
///     problem rather than an untidiness.
///   </item>
///   <item>
///     The acting USER is deliberately NOT carried. Writing it here would hand the SUBJECT of an
///     audit record a key to their own audit trail. Their identity is on the payload, where it is
///     evidence rather than a permission.
///   </item>
///   <item>
///     The record is marked system-emitted, which is a separate field from the tenant — so
///     "framework-emitted AND belonging to this tenant" is stated directly, not traded off.
///   </item>
/// </list>
/// <para>
/// Shared because audit records are written from two independent paths: the emitter and the
/// event-store decorator. They previously each built their own bare hop, which is how both came to
/// omit the scope. One helper means the rule cannot hold in one path and not the other.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/message-security#scope-markers</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditEnvelopeScopeTests.cs</tests>
public static class AuditRecordScope {

  /// <summary>Builds the scope for an audit record about an action in the given tenant.</summary>
  /// <param name="auditedTenantId">
  /// The tenant whose action is being audited, or null when auditing something that belongs to no
  /// tenant — a control-plane event. No tenant is invented in that case.
  /// </param>
  /// <returns>The scope delta for the audit record's hop.</returns>
  public static ScopeDelta? For(string? auditedTenantId) {
    return ScopeDelta.FromPerspectiveScope(new PerspectiveScope {
      TenantId = auditedTenantId,
      IsSystem = true,
    });
  }
}
