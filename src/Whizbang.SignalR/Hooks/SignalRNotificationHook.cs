// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whizbang.Core.Attributes;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;

namespace Whizbang.SignalR.Hooks;

/// <summary>
/// Message tag hook that sends SignalR notifications for events
/// marked with <see cref="SignalTagAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// This hook integrates with ASP.NET Core SignalR to push real-time
/// notifications to connected clients based on message tags.
/// </para>
/// <para>
/// Registration example:
/// <code>
/// services.AddWhizbang(options => {
///   options.Tags.UseHook&lt;SignalTagAttribute, SignalRNotificationHook&gt;();
/// });
/// </code>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Notification to specific group
/// [SignalTag(Tag = "order-shipped", Group = "customer-{CustomerId}", Priority = SignalPriority.High)]
/// public record OrderShippedEvent(Guid OrderId, Guid CustomerId, string TrackingNumber) : IEvent;
///
/// // Broadcast to every connected client: only with the explicit "all" group
/// [SignalTag(Tag = "system-announcement", Group = "all", Priority = SignalPriority.Critical)]
/// public record SystemAnnouncementEvent(string Message) : IEvent;
/// </code>
/// </example>
/// <docs>apis/signalr/notification-hooks</docs>
/// <tests>tests/Whizbang.SignalR.Tests/Hooks/SignalRNotificationHookTests.cs</tests>
/// <typeparam name="THub">The SignalR hub type to use for notifications.</typeparam>
/// <remarks>
/// Creates a new SignalR notification hook.
/// </remarks>
/// <param name="hubContext">The SignalR hub context for sending notifications.</param>
public sealed class SignalRNotificationHook<THub>(
    IHubContext<THub> hubContext,
    ILogger<SignalRNotificationHook<THub>>? logger = null) : IMessageTagHook<SignalTagAttribute>
    where THub : Hub {
  private readonly IHubContext<THub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
  private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

  /// <summary>
  /// Sends the notification to the tag's group: every connected client only for the explicit
  /// <see cref="SignalRGroupResolver.BROADCAST_GROUP"/> (<c>"all"</c>), otherwise the resolved group. A tag with
  /// no group, or a group whose placeholders do not all resolve, sends nothing and logs a warning.
  /// </summary>
  public async ValueTask<JsonElement?> OnTaggedMessageAsync(
      TagContext<SignalTagAttribute> context,
      CancellationToken ct) {
    var attribute = context.Attribute;
    if (string.IsNullOrEmpty(attribute.Group)) {
      SignalRGroupResolver.LogNoGroup(_logger, attribute.Tag, context.MessageType.Name);
      return null;
    }

    IClientProxy target;
    if (string.Equals(attribute.Group, SignalRGroupResolver.BROADCAST_GROUP, StringComparison.OrdinalIgnoreCase)) {
      target = _hubContext.Clients.All;
    } else {
      var groupName = SignalRGroupResolver.Resolve(attribute.Group, context.Payload, context.Scope);
      if (groupName is null) {
        SignalRGroupResolver.LogUnresolvedGroup(_logger, attribute.Tag, context.MessageType.Name, attribute.Group);
        return null;
      }
      target = _hubContext.Clients.Group(groupName);
    }

    var notification = new NotificationMessage {
      Tag = attribute.Tag,
      Priority = attribute.Priority.ToString(),
      MessageType = context.MessageType.Name,
      Payload = context.Payload,
      Timestamp = DateTimeOffset.UtcNow
    };
    await target.SendAsync("ReceiveNotification", notification, ct).ConfigureAwait(false);

    // Return null to pass original payload to next hook
    return null;
  }
}

/// <summary>
/// Resolves a <see cref="SignalTagAttribute.Group"/> template for <see cref="SignalRNotificationHook{THub}"/>.
/// </summary>
/// <remarks>
/// <c>{TenantId}</c> resolves from the message's scope only, so a payload field of that name can never route a
/// notification to another tenant. Every other placeholder resolves from the payload first and then, for
/// <c>{UserId}</c>, <c>{OrganizationId}</c> and <c>{CustomerId}</c>, from the scope: routing to the customer an
/// order belongs to, or to an assigned user, is a payload value. A placeholder with no value makes the whole
/// group unresolved rather than a literal group name every such message would share.
/// </remarks>
/// <docs>fundamentals/messages/message-tags#signal-tag</docs>
internal static partial class SignalRGroupResolver {
  /// <summary>The group value that broadcasts to every connected client.</summary>
  public const string BROADCAST_GROUP = "all";

  private const string TENANT_PLACEHOLDER = "TenantId";

  /// <summary>Returns the resolved group, or <see langword="null"/> when any placeholder has no value.</summary>
  public static string? Resolve(string template, JsonElement payload, IScopeContext? scope) {
    var unresolved = false;
    var result = _placeholder().Replace(template, match => {
      var name = match.Groups[1].Value;
      var value = name == TENANT_PLACEHOLDER
          ? scope?.Scope.TenantId
          : _payloadValue(payload, name) ?? _scopeValue(scope, name);
      if (string.IsNullOrEmpty(value)) {
        unresolved = true;
        return match.Value;
      }
      return value;
    });
    return unresolved ? null : result;
  }

  private static string? _scopeValue(IScopeContext? scope, string name) => name switch {
    "UserId" => scope?.Scope.UserId,
    "OrganizationId" => scope?.Scope.OrganizationId,
    "CustomerId" => scope?.Scope.CustomerId,
    _ => null,
  };

  private static string? _payloadValue(JsonElement payload, string name) {
    if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var prop)) {
      return null;
    }
    return prop.ValueKind switch {
      // GetString returns null only for JsonValueKind.Null, never for this arm.
      JsonValueKind.String => prop.GetString()!,
      JsonValueKind.True => "true",
      JsonValueKind.False => "false",
      _ => prop.GetRawText()
    };
  }

  [GeneratedRegex(@"\{([^{}]+)\}")]
  private static partial Regex _placeholder();

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "SignalR notification {Tag} for {MessageType} was not sent: the tag has no Group. Set Group = \"all\" to broadcast to every connected client, or name a group.")]
  public static partial void LogNoGroup(ILogger logger, string tag, string messageType);

  [LoggerMessage(Level = LogLevel.Warning,
      Message = "SignalR notification {Tag} for {MessageType} was not sent: group template {GroupTemplate} has a placeholder with no value (identity placeholders come from the message's scope, others from its payload).")]
  public static partial void LogUnresolvedGroup(ILogger logger, string tag, string messageType, string groupTemplate);
}

/// <summary>
/// Notification message sent to SignalR clients.
/// </summary>
public sealed record NotificationMessage {
  /// <summary>The notification tag.</summary>
  public required string Tag { get; init; }

  /// <summary>The notification priority.</summary>
  public required string Priority { get; init; }

  /// <summary>The message type name.</summary>
  public required string MessageType { get; init; }

  /// <summary>The message payload.</summary>
  public required JsonElement Payload { get; init; }

  /// <summary>When the notification was sent.</summary>
  public required DateTimeOffset Timestamp { get; init; }
}
