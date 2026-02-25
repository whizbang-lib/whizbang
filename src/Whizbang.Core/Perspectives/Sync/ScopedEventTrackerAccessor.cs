namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Provides ambient access to the current scope's <see cref="IScopedEventTracker"/>.
/// </summary>
/// <remarks>
/// <para>
/// This accessor uses <see cref="AsyncLocal{T}"/> to store the scoped tracker,
/// making it accessible from singleton services like <see cref="Dispatcher"/>
/// that cannot have scoped dependencies injected directly.
/// </para>
/// <para>
/// The tracker is automatically set when <see cref="IScopedEventTracker"/> is resolved
/// from a scope, and cleared when the scope is disposed.
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> AsyncLocal ensures each async execution context
/// has its own tracker instance, providing proper scope isolation.
/// </para>
/// </remarks>
/// <docs>core-concepts/perspectives/perspective-sync#scoped-tracker-accessor</docs>
/// <tests>Whizbang.Core.Tests/Perspectives/Sync/ScopedEventTrackerAccessorTests.cs</tests>
public static class ScopedEventTrackerAccessor {
  private static readonly AsyncLocal<IScopedEventTracker?> _current = new();

  /// <summary>
  /// Gets or sets the current scope's event tracker.
  /// </summary>
  /// <remarks>
  /// Returns <c>null</c> if called outside of a scope or if no tracker has been set.
  /// Setting to <c>null</c> clears the current tracker.
  /// </remarks>
  public static IScopedEventTracker? CurrentTracker {
    get => _current.Value;
    set => _current.Value = value;
  }
}
