using System.Text.Json;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tags;

/// <summary>
/// Processes messages tagged with TAttribute after successful handling.
/// Hooks are executed in priority order (ascending: -100 → 500).
/// </summary>
/// <typeparam name="TAttribute">The tag attribute type to handle.</typeparam>
/// <remarks>
/// <para>
/// Hooks are invoked after a message with matching tag is successfully processed.
/// Hooks can optionally manipulate the payload before it's passed to subsequent hooks.
/// </para>
/// <para>
/// Hooks are registered via <c>options.Tags.UseHook&lt;TAttribute, THook&gt;(priority)</c>
/// in Program.cs. Default priority is -100 (lowest, fires first).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Registration
/// services.AddWhizbang(options => {
///   options.Tags.UseHook&lt;NotificationTagAttribute, SignalRNotificationHook&gt;();
///   options.Tags.UseHook&lt;AuditTagAttribute, AuditLogHook&gt;(priority: -10);
/// });
///
/// // Hook implementation
/// public sealed class SignalRNotificationHook : IMessageTagHook&lt;NotificationTagAttribute&gt; {
///   public async ValueTask&lt;JsonElement?&gt; OnTaggedMessageAsync(
///       TagContext&lt;NotificationTagAttribute&gt; context,
///       CancellationToken ct) {
///     // Process the tagged message
///     await SendNotificationAsync(context.Attribute.Group, context.Payload, ct);
///     return null; // Return null to pass original payload to next hook
///   }
/// }
/// </code>
/// </example>
/// <docs>core-concepts/message-tags#hooks</docs>
/// <tests>Whizbang.Core.Tests/Tags/MessageTagHookTests.cs</tests>
public interface IMessageTagHook<TAttribute> where TAttribute : MessageTagAttribute {
  /// <summary>
  /// Called after a message with matching tag is successfully processed.
  /// Hooks can manipulate the payload before it's passed to subsequent hooks.
  /// </summary>
  /// <param name="context">Context with message, tag, and merged payload.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Optionally modified payload for subsequent hooks, or null to use original.</returns>
  ValueTask<JsonElement?> OnTaggedMessageAsync(TagContext<TAttribute> context, CancellationToken ct);
}
