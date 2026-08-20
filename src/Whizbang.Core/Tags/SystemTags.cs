namespace Whizbang.Core.Tags;

/// <summary>
/// The framework-reserved tag vocabulary. Tags carrying the <see cref="ReservedPrefix"/> belong
/// to Whizbang itself (following the <c>$wb-system</c> / <c>wh_</c> naming family); startup
/// validation rejects application-declared tag attributes that mint new <c>sys-*</c> tags, so
/// framework and application tag namespaces can never collide.
/// </summary>
/// <remarks>
/// Applying an EXISTING framework tag (e.g. <see cref="Audit"/>) to an application message type
/// is allowed — that is deliberate opt-in to the framework policy bound to it, not a new
/// reservation. Only minting an unknown <c>sys-*</c> tag fails validation
/// (<see cref="TagPolicyValidator"/>).
/// </remarks>
/// <docs>fundamentals/messages/message-tags#system-tags</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/SystemTagsTests.cs:Audit_IsSysAuditAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/SystemTagsTests.cs:IsFrameworkTag_RejectsUnknownSysTagAsync</tests>
public static class SystemTags {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>
  /// The reserved prefix for framework tags. Application tag attributes may not mint new tags
  /// under this prefix.
  /// </summary>
  public const string RESERVED_PREFIX = "sys-";
#pragma warning restore CA1707

  /// <summary>
  /// The framework audit tag carried by audit system events (<c>EventAudited</c> /
  /// <c>CommandAudited</c>). <c>EnableAudit()</c> binds the built-in audit coalesce policy to
  /// this tag; a host binding for the same tag replaces it.
  /// </summary>
  public const string AUDIT = "sys-audit";

  /// <summary>
  /// The framework control-plane traffic class (transport traffic classes, topology arc phase 9).
  /// Carried by the SUPERSEDABLE control signals — the integrity checkpoint / manifest /
  /// re-delivery-request families in <c>Whizbang.Core.Messaging</c>, every one of which is
  /// re-derived on the next cadence. Binding it with
  /// <see cref="TagOptions.RouteNamespace(string, string)"/> moves the class to its own broker
  /// namespace; <c>ControlClassOptions</c> binds its delivery semantics (short TTL, sessionless
  /// subscriptions, non-durable receive).
  /// </summary>
  /// <remarks>
  /// Deliberately NOT carried by <c>Whizbang.Core.Commands.System</c>'s durable system commands
  /// (run-control, killswitches, rebuild/reseed) — those are one-shot operator intent that a TTL
  /// would silently lose — nor by <c>Whizbang.Core.Minting</c>'s composite envelopes, which are
  /// wire-only wrappers around durable payload. <c>IControlPlaneMessage</c> is a security/dead-letter
  /// marker, not a traffic class: the two sets deliberately differ.
  /// </remarks>
  public const string CONTROL = "sys-control";

  /// <summary>
  /// Whether <paramref name="tag"/> is a member of the framework's own tag set — the exemption
  /// the reserved-prefix validation consults.
  /// </summary>
  /// <param name="tag">The tag value to test.</param>
  /// <returns><c>true</c> for tags the framework itself ships; <c>false</c> otherwise.</returns>
  public static bool IsFrameworkTag(string tag) => tag is AUDIT or CONTROL;
}
