using System.Text.Json;
using Whizbang.Core.Attributes;

namespace Whizbang.Core.Tags;

/// <summary>
/// Context provided to tag hooks with message and tag information.
/// Contains the merged payload built from extracted properties, event data, and extra JSON.
/// </summary>
/// <typeparam name="TAttribute">The specific tag attribute type.</typeparam>
/// <remarks>
/// <para>
/// The payload is built from:
/// <list type="bullet">
/// <item><description>Extracted properties from the <see cref="MessageTagAttribute.Properties"/> array</description></item>
/// <item><description>Full event under "__event" key when <see cref="MessageTagAttribute.IncludeEvent"/> is true</description></item>
/// <item><description>Merged content from <see cref="MessageTagAttribute.ExtraJson"/> if specified</description></item>
/// </list>
/// </para>
/// <para>
/// Hooks can optionally modify the payload by returning a new <see cref="JsonElement"/>
/// from their <see cref="IMessageTagHook{TAttribute}.OnTaggedMessageAsync"/> method.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public async ValueTask&lt;JsonElement?&gt; OnTaggedMessageAsync(
///     TagContext&lt;NotificationTagAttribute&gt; context,
///     CancellationToken ct) {
///   // Access the attribute
///   var group = context.Attribute.Group;
///   var priority = context.Attribute.Priority;
///
///   // Access the payload
///   var payload = context.Payload;
///
///   // Access scope data
///   var tenantId = context.Scope?["TenantId"]?.ToString();
///
///   return null;
/// }
/// </code>
/// </example>
/// <docs>core-concepts/message-tags#tag-context</docs>
/// <tests>Whizbang.Core.Tests/Tags/TagContextTests.cs</tests>
public sealed record TagContext<TAttribute> where TAttribute : MessageTagAttribute {
  /// <summary>
  /// Gets the attribute instance from the message type.
  /// Contains all tag-specific configuration like Tag name, Properties, Group, etc.
  /// </summary>
  public required TAttribute Attribute { get; init; }

  /// <summary>
  /// Gets the base attribute type for generic handling.
  /// Useful when handling multiple tag types in a single hook.
  /// </summary>
  public Type AttributeType => typeof(TAttribute);

  /// <summary>
  /// Gets the message that was processed.
  /// This is the original event or command that triggered the hook.
  /// </summary>
  public required object Message { get; init; }

  /// <summary>
  /// Gets the message type.
  /// Useful for logging and debugging purposes.
  /// </summary>
  public required Type MessageType { get; init; }

  /// <summary>
  /// Gets the merged payload containing extracted properties, event data, and extra JSON.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The payload structure depends on the tag attribute configuration:
  /// </para>
  /// <code>
  /// // Example payload when Properties = ["JobId", "Status"], IncludeEvent = true, ExtraJson = {"source": "api"}
  /// {
  ///   "JobId": "abc-123",
  ///   "Status": "Completed",
  ///   "__event": { "JobId": "abc-123", "Status": "Completed", "Details": {...} },
  ///   "source": "api"
  /// }
  /// </code>
  /// </remarks>
  public required JsonElement Payload { get; init; }

  /// <summary>
  /// Gets the event scope containing tenant, user, and other contextual data.
  /// Populated from the event's scope field when available.
  /// </summary>
  public IReadOnlyDictionary<string, object?>? Scope { get; init; }
}
