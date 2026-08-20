using Whizbang.Core.Attributes;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The framework's control-class tag attribute: marks a message as a member of the
/// <c>SystemTags.CONTROL</c> ("sys-control") traffic class. The class changes DELIVERY SEMANTICS,
/// not just location — short TTL at mint, sessionless subscriptions, and a non-durable receive
/// path — and, when a host binds <c>options.Tags.RouteNamespace("sys-control", …)</c>, it also
/// moves the class onto its own broker namespace with its own request quota and failure domain.
/// </summary>
/// <remarks>
/// <para>
/// Carried ONLY by supersedable control signals: a member's value expires because the next cadence
/// re-derives it, so a superseded copy expiring on the broker costs nothing and a control backlog
/// becomes structurally impossible. Durable system commands (<c>Whizbang.Core.Commands.System</c>)
/// and composite envelopes (<c>Whizbang.Core.Minting</c>) deliberately stay out — see
/// <c>SystemTags.CONTROL</c>.
/// </para>
/// <para>
/// Usage must set the tag explicitly — <c>[SystemControlTag(Tag = SystemTags.CONTROL, Properties = [])]</c>
/// — rather than a constructor default: the MessageTagDiscoveryGenerator reads only what is
/// syntactically present at the usage site, so a constructor-assigned tag would register as an empty
/// string (the known <c>AuditEventAttribute</c> pitfall). <c>Properties = []</c> opts the type out of
/// hook-payload extraction entirely: this tag classifies traffic, it never feeds a notification
/// payload, and control bodies (manifest digest pages, checkpoint buckets) are large.
/// </para>
/// </remarks>
/// <docs>fundamentals/messages/message-tags#system-tags</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/SystemControlTagTests.cs:SupersedableControlSignals_CarryTheControlTagAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Tags/SystemControlTagTests.cs:ControlClassMembership_IsExactlyTheMessagingControlFamiliesAsync</tests>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public sealed class SystemControlTagAttribute : MessageTagAttribute {
}
