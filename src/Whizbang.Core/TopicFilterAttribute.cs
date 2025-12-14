namespace Whizbang.Core;

/// <summary>
/// Marks a command with topic filters for message routing.
/// Can be applied multiple times for commands that should be routed to multiple topics.
/// Topic filters are extracted at compile-time via source generation to create AOT-compatible routing tables.
/// </summary>
/// <docs>messaging/topic-filters</docs>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public class TopicFilterAttribute : Attribute {
  /// <summary>
  /// The topic filter string for routing this command.
  /// </summary>
  public string Filter { get; }

  /// <summary>
  /// Creates a new topic filter attribute with the specified filter string.
  /// </summary>
  /// <param name="filter">The topic filter string (e.g., "orders.create", "payments.process")</param>
  public TopicFilterAttribute(string filter) {
    Filter = filter ?? throw new ArgumentNullException(nameof(filter));
  }
}
