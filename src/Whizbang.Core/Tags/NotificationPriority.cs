namespace Whizbang.Core.Tags;

/// <summary>
/// Defines the priority level for real-time notifications.
/// Higher priority notifications may be delivered more prominently or with different visual treatments.
/// </summary>
/// <docs>core-concepts/message-tags#notification-priority</docs>
public enum NotificationPriority {
  /// <summary>
  /// Low priority notifications for background or non-urgent updates.
  /// May be batched or delayed for efficiency.
  /// </summary>
  Low = 0,

  /// <summary>
  /// Normal priority for standard notifications (default).
  /// Delivered in real-time without special treatment.
  /// </summary>
  Normal = 1,

  /// <summary>
  /// High priority for important notifications requiring user attention.
  /// May trigger more prominent visual or audio cues.
  /// </summary>
  High = 2,

  /// <summary>
  /// Critical priority for urgent system alerts or failures.
  /// Should trigger immediate attention and may bypass user preferences.
  /// </summary>
  Critical = 3
}
