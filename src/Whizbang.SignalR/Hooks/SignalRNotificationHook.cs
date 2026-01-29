using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Whizbang.Core.Attributes;
using Whizbang.Core.Tags;

namespace Whizbang.SignalR.Hooks;

/// <summary>
/// Message tag hook that sends SignalR notifications for events
/// marked with <see cref="NotificationTagAttribute"/>.
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
///   options.Tags.UseHook&lt;NotificationTagAttribute, SignalRNotificationHook&gt;();
/// });
/// </code>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Notification to specific group
/// [NotificationTag(Tag = "order-shipped", Group = "customer-{CustomerId}", Priority = NotificationPriority.High)]
/// public record OrderShippedEvent(Guid OrderId, Guid CustomerId, string TrackingNumber) : IEvent;
///
/// // Broadcast notification
/// [NotificationTag(Tag = "system-announcement", Priority = NotificationPriority.Critical)]
/// public record SystemAnnouncementEvent(string Message) : IEvent;
/// </code>
/// </example>
/// <docs>signalr/notification-hooks</docs>
/// <tests>Whizbang.SignalR.Tests/Hooks/SignalRNotificationHookTests.cs</tests>
/// <typeparam name="THub">The SignalR hub type to use for notifications.</typeparam>
public sealed class SignalRNotificationHook<THub> : IMessageTagHook<NotificationTagAttribute>
    where THub : Hub {
  private readonly IHubContext<THub> _hubContext;

  /// <summary>
  /// Creates a new SignalR notification hook.
  /// </summary>
  /// <param name="hubContext">The SignalR hub context for sending notifications.</param>
  public SignalRNotificationHook(IHubContext<THub> hubContext) {
    _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
  }

  /// <summary>
  /// Sends a SignalR notification for the tagged message.
  /// </summary>
  public async ValueTask<JsonElement?> OnTaggedMessageAsync(
      TagContext<NotificationTagAttribute> context,
      CancellationToken ct) {
    var attribute = context.Attribute;
    var groupName = _resolveGroup(attribute.Group, context.Payload, context.Scope);

    var notification = new NotificationMessage {
      Tag = attribute.Tag,
      Priority = attribute.Priority.ToString(),
      MessageType = context.MessageType.Name,
      Payload = context.Payload,
      Timestamp = DateTimeOffset.UtcNow
    };

    if (string.IsNullOrEmpty(groupName)) {
      // Broadcast to all clients
      await _hubContext.Clients.All.SendAsync(
        "ReceiveNotification",
        notification,
        ct
      ).ConfigureAwait(false);
    } else {
      // Send to specific group
      await _hubContext.Clients.Group(groupName).SendAsync(
        "ReceiveNotification",
        notification,
        ct
      ).ConfigureAwait(false);
    }

    // Return null to pass original payload to next hook
    return null;
  }

  private static string? _resolveGroup(
      string? template,
      JsonElement payload,
      IReadOnlyDictionary<string, object?>? scope) {
    if (string.IsNullOrEmpty(template)) {
      return null;
    }

    var result = template;

    // Replace {PropertyName} placeholders with payload values
    if (payload.ValueKind == JsonValueKind.Object) {
      foreach (var prop in payload.EnumerateObject()) {
        var placeholder = $"{{{prop.Name}}}";
        if (result.Contains(placeholder, StringComparison.Ordinal)) {
          var value = prop.Value.ValueKind switch {
            JsonValueKind.String => prop.Value.GetString() ?? "",
            JsonValueKind.Number => prop.Value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => prop.Value.GetRawText()
          };
          result = result.Replace(placeholder, value, StringComparison.Ordinal);
        }
      }
    }

    // Also replace from scope
    if (scope is not null) {
      foreach (var (key, value) in scope) {
        var placeholder = $"{{{key}}}";
        if (result.Contains(placeholder, StringComparison.Ordinal)) {
          result = result.Replace(placeholder, value?.ToString() ?? "", StringComparison.Ordinal);
        }
      }
    }

    return result;
  }
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
