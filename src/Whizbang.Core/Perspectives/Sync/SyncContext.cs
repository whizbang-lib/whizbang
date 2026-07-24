namespace Whizbang.Core.Perspectives.Sync;

/// <summary>
/// Provides sync status information to handlers.
/// Inject via constructor to access sync results.
/// </summary>
/// <remarks>
/// <para>
/// Handlers can optionally inject <see cref="SyncContext"/> to access information about
/// the perspective sync that was performed before the handler was invoked.
/// </para>
/// <para>
/// This is particularly useful when using <see cref="SyncFireBehavior.FireAlways"/>,
/// as the handler can check <see cref="IsSuccess"/> or <see cref="IsTimedOut"/> to
/// determine the sync outcome and handle it appropriately.
/// </para>
/// <code>
/// [AwaitPerspectiveSync(typeof(OrderProjection), FireBehavior = SyncFireBehavior.FireAlways)]
/// public class GetOrderHandler : IReceptor&lt;GetOrderQuery, Order?&gt; {
///   private readonly SyncContext? _syncContext;
///
///   public GetOrderHandler(SyncContext? syncContext = null) {
///     _syncContext = syncContext;
///   }
///
///   public async Task&lt;Order?&gt; HandleAsync(GetOrderQuery query, CancellationToken ct) {
///     if (_syncContext?.IsTimedOut == true) {
///       _logger.LogWarning("Sync timed out for stream {StreamId}", _syncContext.StreamId);
///       // Return potentially stale data, or throw, or handle gracefully
///     }
///     return await _repository.GetByIdAsync(query.OrderId, ct);
///   }
/// }
/// </code>
/// </remarks>
/// <docs>fundamentals/perspectives/perspective-sync#sync-context</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Sync/SyncContextTests.cs</tests>
public sealed class SyncContext {
  /// <summary>
  /// Gets the stream ID that was synced.
  /// </summary>
  public Guid StreamId { get; init; }

  /// <summary>
  /// Gets the perspective type that was synced.
  /// </summary>
  public Type PerspectiveType { get; init; } = null!;

  /// <summary>
  /// Gets the sync outcome.
  /// </summary>
  public SyncOutcome Outcome { get; init; }

  /// <summary>
  /// Gets the total number of events that were awaited.
  /// </summary>
  public int EventsAwaited { get; init; }

  /// <summary>
  /// Gets the time spent waiting for sync.
  /// </summary>
  public TimeSpan ElapsedTime { get; init; }

  /// <summary>
  /// Gets a value indicating whether sync completed successfully.
  /// </summary>
  /// <value><c>true</c> if the outcome is <see cref="SyncOutcome.Synced"/>; otherwise, <c>false</c>.</value>
  public bool IsSuccess => Outcome == SyncOutcome.Synced;

  /// <summary>
  /// Gets a value indicating whether sync timed out.
  /// </summary>
  /// <value><c>true</c> if the outcome is <see cref="SyncOutcome.TimedOut"/>; otherwise, <c>false</c>.</value>
  public bool IsTimedOut => Outcome == SyncOutcome.TimedOut;

  /// <summary>
  /// Gets the reason for failure if not success.
  /// </summary>
  /// <value>A message describing the failure, or <c>null</c> if sync was successful.</value>
  public string? FailureReason { get; init; }
}
