using Whizbang.Core.Transports;

namespace Whizbang.Core.Resilience;

/// <summary>
/// Represents the possible states of a subscription.
/// </summary>
/// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
public enum SubscriptionStatus {
  /// <summary>
  /// Initial state - subscription has not been attempted yet.
  /// </summary>
  Pending = 0,

  /// <summary>
  /// Subscription failed and is being retried with exponential backoff.
  /// </summary>
  Recovering,

  /// <summary>
  /// Subscription is active and receiving messages.
  /// </summary>
  Healthy,

  /// <summary>
  /// Subscription has permanently failed (only when RetryIndefinitely=false).
  /// </summary>
  Failed
}

/// <summary>
/// Tracks the state of a subscription to a transport destination.
/// </summary>
/// <remarks>
/// <para>
/// Used by <see cref="Workers.TransportConsumerWorker"/> to track subscription status,
/// retry attempts, and errors for each destination. This enables partial failure handling
/// where the worker can continue processing with some subscriptions while others are recovering.
/// </para>
/// </remarks>
/// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
/// <tests>tests/Whizbang.Core.Tests/Resilience/SubscriptionStateTests.cs</tests>
/// <remarks>
/// Initializes a new instance of <see cref="SubscriptionState"/> for the specified destination.
/// </remarks>
/// <param name="destination">The transport destination being tracked.</param>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="destination"/> is null.</exception>
public class SubscriptionState(TransportDestination destination) {

  /// <summary>
  /// The transport destination this state tracks.
  /// </summary>
  public TransportDestination Destination { get; } = destination ?? throw new ArgumentNullException(nameof(destination));

  /// <summary>
  /// Current status of the subscription.
  /// </summary>
  public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Pending;

  /// <summary>
  /// Number of subscription attempts made. Used for logging phase transitions.
  /// </summary>
  public int AttemptCount { get; set; }

  /// <summary>
  /// The most recent exception that caused a subscription failure.
  /// </summary>
  public Exception? LastError { get; set; }

  /// <summary>
  /// When the most recent error occurred.
  /// </summary>
  public DateTimeOffset? LastErrorTime { get; set; }

  /// <summary>
  /// Reference to the active subscription, if any.
  /// </summary>
  public ISubscription? Subscription { get; set; }

  /// <summary>
  /// Increments the attempt count by one.
  /// </summary>
  public void IncrementAttempt() => AttemptCount++;

  /// <summary>
  /// Resets the attempt count to zero.
  /// </summary>
  public void ResetAttempts() => AttemptCount = 0;
}
