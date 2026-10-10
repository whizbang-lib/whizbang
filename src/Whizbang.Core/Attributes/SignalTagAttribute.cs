// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Tags;

namespace Whizbang.Core.Attributes;

/// <summary>
/// Tags a message for real-time signal delivery (SignalR, WebSockets, etc.).
/// Discovered by MessageTagDiscoveryGenerator for AOT-compatible registration.
/// </summary>
/// <remarks>
/// <para>
/// Signals are delivered through registered <c>IMessageTagHook&lt;SignalTagAttribute&gt;</c>
/// implementations. The built-in SignalRNotificationHook (in Whizbang.SignalR) sends signals
/// to the specified group with the constructed payload.
/// </para>
/// <para>
/// The <see cref="Group"/> property supports {PropertyName} placeholders that are replaced
/// with values from the event at runtime. <c>{TenantId}</c> always comes from the message's scope, never
/// from the event, so a message cannot route a notification to another tenant. A tag with no
/// <see cref="Group"/>, or a group with a placeholder that has no value, is not sent.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [SignalTag(
///     Tag = "order-shipped",
///     Properties = ["OrderId", "CustomerId", "TrackingNumber"],
///     Group = "customer-{CustomerId}",
///     Priority = SignalPriority.High)]
/// public sealed record OrderShippedEvent(Guid OrderId, Guid CustomerId, string TrackingNumber);
/// </code>
/// </example>
/// <docs>fundamentals/messages/message-tags#signal-tag</docs>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = true)]
public sealed class SignalTagAttribute : MessageTagAttribute {
  /// <summary>
  /// Gets or sets the target group/channel for the notification. Required: a tag without a group is not
  /// sent. Supports {PropertyName} placeholders for dynamic group resolution.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>{TenantId}</c> resolves from the message's scope only. Other placeholders resolve from the event, and
  /// <c>{UserId}</c>, <c>{OrganizationId}</c> and <c>{CustomerId}</c> fall back to the scope. A placeholder with no
  /// value means the notification is not sent.
  /// </para>
  /// Examples:
  /// <list type="bullet">
  /// <item><description>"all" - broadcasts to every connected client, of every tenant; the only way to broadcast</description></item>
  /// <item><description>"tenant-{TenantId}" - targets a specific tenant's clients</description></item>
  /// <item><description>"customer-{CustomerId}" - targets a specific customer</description></item>
  /// <item><description>"user-{UserId}" - targets a specific user</description></item>
  /// </list>
  /// </remarks>
  public string? Group { get; init; }

  /// <summary>
  /// Gets or sets the signal priority.
  /// Defaults to <see cref="SignalPriority.Normal"/>.
  /// </summary>
  /// <remarks>
  /// Higher priority signals may receive different visual treatment,
  /// bypass quiet hours, or trigger additional delivery channels.
  /// </remarks>
  public SignalPriority Priority { get; init; } = SignalPriority.Normal;
}
