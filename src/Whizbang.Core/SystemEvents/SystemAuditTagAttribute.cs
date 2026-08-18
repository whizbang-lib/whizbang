using Whizbang.Core.Attributes;

namespace Whizbang.Core.SystemEvents;

/// <summary>
/// The framework's audit tag attribute: marks a system event as a member of the
/// <c>SystemTags.AUDIT</c> ("sys-audit") coalesce group. Carried by <see cref="EventAudited"/>
/// and <see cref="CommandAudited"/>; <c>EnableAudit()</c> binds the built-in audit coalesce
/// policy to the tag, so audit singles ride the generic tag-bound coalescing machinery — there
/// is no audit-specific shipping code.
/// </summary>
/// <remarks>
/// Usage must set the tag explicitly — <c>[SystemAuditTag(Tag = SystemTags.AUDIT)]</c> — rather
/// than a constructor default: the MessageTagDiscoveryGenerator reads only what is syntactically
/// present at the usage site, so a constructor-assigned tag would register as an empty string
/// (the known <c>AuditEventAttribute</c> pitfall). Keeping <c>Tag</c> required makes the correct
/// usage the only compilable one.
/// </remarks>
/// <docs>fundamentals/security/audit-logging#audit-shipping</docs>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditCoalesceRebaseTests.cs:EventAudited_CarriesTheSysAuditTag_InTheGeneratedRegistryAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/SystemEvents/AuditCoalesceRebaseTests.cs:CommandAudited_CarriesTheSysAuditTag_InTheGeneratedRegistryAsync</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class SystemAuditTagAttribute : MessageTagAttribute {
}
