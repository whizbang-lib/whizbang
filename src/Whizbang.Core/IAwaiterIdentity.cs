namespace Whizbang.Core;

/// <summary>
/// Marker interface for awaiters that have a unique identity.
/// Enables per-awaiter tracking, cleanup on cancellation, and diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// Every awaiter instance gets a unique <see cref="AwaiterId"/> at construction time.
/// This ID is used to key waiter registrations in <see cref="Perspectives.Sync.ISyncEventTracker"/>,
/// allowing precise cleanup when an awaiter is cancelled or disposed — without affecting
/// other awaiters waiting on the same events.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync#awaiter-identity</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/AwaiterIdentityTests.cs</tests>
public interface IAwaiterIdentity {
  /// <summary>
  /// Gets the unique identifier for this awaiter instance.
  /// </summary>
  Guid AwaiterId { get; }
}
