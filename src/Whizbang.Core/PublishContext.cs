namespace Whizbang.Core;

/// <summary>
/// Defines the timing context for event publishing in relation to transaction boundaries.
/// Used for dual publishing patterns where events need to be published at different lifecycle stages.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Dual Publishing Pattern:</strong>
/// </para>
/// <list type="bullet">
///   <item>
///     <term>Immediate (Pre-Commit)</term>
///     <description>
///     Events are published immediately, before the transaction commits.
///     Use this for immediate consistency requirements where downstream systems
///     need to be notified before the transaction is durable.
///     Risk: If transaction fails, compensating actions may be needed.
///     </description>
///   </item>
///   <item>
///     <term>PostCommit</term>
///     <description>
///     Events are published after the transaction successfully commits.
///     Use this for eventual consistency where durability is guaranteed before notification.
///     This is the safer pattern but introduces slight latency.
///     </description>
///   </item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// // Immediate publishing - notify before commit
/// await dispatcher.PublishAsync(orderCreatedEvent, PublishContext.Immediate);
/// await uow.CommitAsync();
///
/// // Post-commit publishing - notify after commit
/// await uow.CommitAsync();
/// await dispatcher.PublishAsync(orderCreatedEvent, PublishContext.PostCommit);
/// </code>
/// </example>
public enum PublishContext {
  /// <summary>
  /// Publish immediately, before transaction commit.
  /// Use for immediate consistency requirements.
  /// Downstream systems are notified before data is durably persisted.
  /// </summary>
  Immediate = 0,

  /// <summary>
  /// Publish after transaction commit.
  /// Use for eventual consistency with durability guarantees.
  /// Downstream systems are notified only after data is durably persisted.
  /// This is the safer pattern for most scenarios.
  /// </summary>
  PostCommit = 1
}
