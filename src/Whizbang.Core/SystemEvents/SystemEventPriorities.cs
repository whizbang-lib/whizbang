using Whizbang.Core.SystemEvents.Security;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// The band each kind of system event is written on.
/// </summary>
/// <remarks>
/// <para>
/// One blanket band was defensible while every band still ran promptly. The idle band is not like
/// that: it is deliberately withheld while the service is busy, so "which band" now decides
/// whether a record is written during an incident or after it. That is not a decision to make for
/// every system event at once.
/// </para>
/// <para>
/// Auditing is the volume and nobody waits for it, so it follows
/// <see cref="SystemEventOptions.AuditPriority"/>, which is the idle band unless the application
/// says otherwise. Security events do NOT follow it there. A denial, a permission change or a
/// scope establishment is evidence someone may be acting on right now, and a band that exists to
/// be withheld under load is exactly the wrong place for the record of a load that looks like an
/// attack. They stay on the background band, and they are listed here explicitly rather than left
/// to the default, because the default is what a later edit is most likely to change.
/// </para>
/// <para>
/// The fallback is BACKGROUND, never idle, so a system event type added later is never silently
/// withheld: moving one into the idle band is a decision someone has to write down here.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/SystemEventPrioritiesTests.cs</tests>
internal static class SystemEventPriorities {
  private static readonly HashSet<Type> _audit = [typeof(EventAudited), typeof(CommandAudited)];

  private static readonly HashSet<Type> _security = [
    typeof(AccessDenied),
    typeof(AccessGranted),
    typeof(PermissionChanged),
    typeof(ScopeContextEstablished),
  ];

  /// <summary>Whether <paramref name="systemEventType"/> is an audit record.</summary>
  public static bool IsAudit(Type systemEventType) => _audit.Contains(systemEventType);

  /// <summary>Whether <paramref name="systemEventType"/> is a security record, which is never withheld.</summary>
  public static bool IsSecurity(Type systemEventType) => _security.Contains(systemEventType);

  /// <summary>The band <paramref name="systemEventType"/> is written on.</summary>
  /// <param name="systemEventType">The system event being emitted.</param>
  /// <param name="options">Where the audit band is configured.</param>
  public static int For(Type systemEventType, SystemEventOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    return IsAudit(systemEventType) ? options.AuditPriority : Priority.WorkPriority.BACKGROUND;
  }
}
