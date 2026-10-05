// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Security;

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
///     TagContext&lt;SignalTagAttribute&gt; context,
///     CancellationToken ct) {
///   // Access the attribute
///   var group = context.Attribute.Group;
///   var priority = context.Attribute.Priority;
///
///   // Access the payload
///   var payload = context.Payload;
///
///   // Access scope data (tenant, user, roles, permissions)
///   var tenantId = context.Scope?.Scope?.TenantId;
///   var userId = context.Scope?.Scope?.UserId;
///   var hasAdminRole = context.Scope?.HasRole("Admin") ?? false;
///
///   return null;
/// }
/// </code>
/// </example>
/// <docs>fundamentals/messages/message-tags#tag-context</docs>
/// <tests>tests/Whizbang.Core.Tests/Tags/TagContextTests.cs</tests>
public sealed record TagContext<TAttribute> : ITagContextChanges where TAttribute : MessageTagAttribute {
  private MessageChanges? _changes;

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
  /// // Example payload when Properties = ["JobId", "Status"], ExtraJson = {"source": "api"}
  /// {
  ///   "JobId": "abc-123",
  ///   "Status": "Completed",
  ///   "source": "api"
  /// }
  /// </code>
  /// </remarks>
  public required JsonElement Payload { get; init; }

  /// <summary>
  /// Gets the security scope context containing tenant, user, roles, permissions, and other contextual data.
  /// Populated from the message envelope's security context when available.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Use this to access security information:
  /// </para>
  /// <code>
  /// var tenantId = context.Scope?.Scope?.TenantId;
  /// var hasPermission = context.Scope?.HasPermission(SomePermission) ?? false;
  /// </code>
  /// </remarks>
  public IScopeContext? Scope { get; init; }

  /// <summary>
  /// Gets the lifecycle stage at which this hook is being invoked.
  /// Hooks can inspect this to decide whether to act (e.g., only fire at PostPerspectiveInline).
  /// </summary>
  public LifecycleStage Stage { get; init; }

  /// <summary>
  /// Which fields the message changed (#1045): for a collective event, the properties its specs assigned on each model,
  /// read from their setters as they applied; for a per-stream event, the event's own properties; for a composite, its
  /// inner events'. One way to ask, whatever the message.
  /// </summary>
  /// <remarks>
  /// A collective event's changes are filled at the stages after its specs applied (<c>PostAllPerspectives</c> and
  /// <c>PostLifecycle</c>); before that, <see cref="MessageChanges.ByModel"/> is empty. A setter that points a row at
  /// another record reports that key, not the referenced record's fields.
  /// </remarks>
  /// <docs>fundamentals/messages/message-tags#changed-properties</docs>
  /// <tests>tests/Whizbang.Core.Tests/Tags/MessageChangesTests.cs</tests>
  public MessageChanges Changes {
    get => _changes ??= MessageChanges.For(Message, MessageType);
    init => _changes = value;
  }

  void ITagContextChanges.AssignChanges(MessageChanges changes) => _changes = changes;
}

/// <summary>Lets the tag processor attach a message's changes to a context whatever its attribute type.</summary>
internal interface ITagContextChanges {
  void AssignChanges(MessageChanges changes);
}
