// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Transports;

namespace Whizbang.Core.Resilience;

/// <summary>
/// Represents the possible states of a subscription.
/// </summary>
/// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
/// <tests>tests/Whizbang.Core.Tests/Resilience/SubscriptionStateTests.cs:SubscriptionStatus_HasExpectedValuesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Resilience/SubscriptionStateTests.cs:Status_SetToRecovering_UpdatesStatusAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/HealthChecks/SubscriptionHealthCheckTests.cs:CheckHealthAsync_SomeRecovering_ReturnsDegradedAsync</tests>
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
  public TransportDestination Destination { get; } = ArgumentGuard.NotNull(destination);

  private SubscriptionStatus _status = SubscriptionStatus.Pending;

  /// <summary>
  /// Current status of the subscription.
  /// </summary>
  /// <remarks>
  /// A write that changes the value is reported to <see cref="Workers.TransportConsumerWorker.SubscriptionStatusChanged"/>
  /// for a state the worker owns; writing the value the state already has reports nothing. Write the fields that
  /// accompany a status (<see cref="LastError"/>, <see cref="AttemptCount"/>, <see cref="Subscription"/>) first, so
  /// an observer of the change reads them already recorded.
  /// </remarks>
  /// <docs>messaging/transports/transport-consumer#subscription-status-changes</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWorkerSubscriptionStatusChangedTests.cs:SubscriptionStatusChanged_StatusWrittenDirectly_RaisedOnlyWhenTheValueChangesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Resilience/SubscriptionStateTests.cs:Status_SetToRecovering_UpdatesStatusAsync</tests>
  public SubscriptionStatus Status {
    get => _status;
    set {
      var previous = Interlocked.Exchange(ref _status, value);
      if (previous != value) {
        StatusChanged?.Invoke(this, previous, value);
      }
    }
  }

  /// <summary>
  /// Called after <see cref="Status"/> changes value, with the state, the status it had before and the one it was
  /// changed to (passed rather than re-read, so a later concurrent write cannot be reported as this one). Set by the
  /// owning <see cref="Workers.TransportConsumerWorker"/>, which turns it into its public
  /// <see cref="Workers.TransportConsumerWorker.SubscriptionStatusChanged"/> event.
  /// </summary>
  internal Action<SubscriptionState, SubscriptionStatus, SubscriptionStatus>? StatusChanged { get; set; }

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

  private ISubscription? _subscription;

  /// <summary>
  /// Reference to the active subscription, if any.
  /// </summary>
  public ISubscription? Subscription {
    get => Volatile.Read(ref _subscription);
    set => Volatile.Write(ref _subscription, value);
  }

  /// <summary>
  /// Takes the subscription out of the state, leaving none, and returns it for the caller to dispose.
  /// </summary>
  /// <remarks>
  /// Connection recovery, shutdown and the per-destination reconnect each dispose what they take. The take is atomic,
  /// so of two paths that race for one subscription exactly one gets it and it is disposed exactly once (#1839).
  /// </remarks>
  /// <returns>The subscription the state held, or <see langword="null"/> when it held none.</returns>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWorkerSubscriptionStatusChangedTests.cs:SubscriptionDisconnects_ReconnectDisposesTheReplacedSubscriptionAndShutdownDoesNotDisposeItAgainAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Workers/TransportConsumerWorkerSubscriptionStatusChangedTests.cs:SubscriptionDisconnects_ConnectionRecoveredAfterTheReconnect_DisposesOnlyTheLiveSubscriptionAsync</tests>
  internal ISubscription? ReleaseSubscription() => Interlocked.Exchange(ref _subscription, null);

  /// <summary>
  /// Takes <paramref name="subscription"/> out of the state only while the state still holds it.
  /// </summary>
  /// <param name="subscription">The subscription the caller expects the state to hold.</param>
  /// <returns><see langword="true"/> when the state held it and now holds none; the caller then disposes it.</returns>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Resilience/SubscriptionRetryHelperDisconnectWindowTests.cs:SubscribeWithRetryAsync_ReconnectAfterADisconnect_DisposesTheReplacedSubscriptionOnceAndDetachesItsHandlerAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Resilience/SubscriptionRetryHelperDisconnectWindowTests.cs:SubscribeWithRetryAsync_ReplacedSubscriptionTakenFromTheStateBeforeTheReconnectRuns_IsNotDisposedByTheReconnectAsync</tests>
  internal bool TryReleaseSubscription(ISubscription subscription) =>
    ReferenceEquals(Interlocked.CompareExchange(ref _subscription, null, subscription), subscription);

  private readonly Lock _reconnectGate = new();
  private TimeSpan? _lastReconnectDelay;
  private DateTimeOffset _healthySince;

  /// <summary>
  /// Records when the current subscription became healthy, the start of the stretch
  /// <see cref="NextReconnectDelay"/> measures.
  /// </summary>
  /// <param name="at">When the subscription was established.</param>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Resilience/SubscriptionRetryHelperReconnectBackoffTests.cs</tests>
  internal void MarkHealthy(DateTimeOffset at) {
    lock (_reconnectGate) {
      _healthySince = at;
    }
  }

  /// <summary>
  /// The delay the reconnect after a disconnect at <paramref name="now"/> waits, remembered as the last one (#1840).
  /// </summary>
  /// <remarks>
  /// The first reconnect, and the first after a subscription stayed healthy for
  /// <see cref="SubscriptionResilienceOptions.ReconnectBackoffResetAfter"/>, waits
  /// <see cref="SubscriptionResilienceOptions.InitialRetryDelay"/>. One whose subscription dropped sooner waits the
  /// last reconnect's delay grown by <see cref="SubscriptionRetryHelper.CalculateNextDelay"/>, so a destination that
  /// fails right after every successful subscribe backs off across the cycles instead of reconnecting at the initial
  /// delay forever. Connection recovery and the health monitor leave the streak alone: they subscribe again, which
  /// starts a new healthy stretch, and the next disconnect is judged by how long that stretch lasted.
  /// </remarks>
  /// <param name="now">When the disconnect was accepted.</param>
  /// <param name="options">The backoff settings.</param>
  /// <returns>How long the reconnect waits before subscribing again.</returns>
  /// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Resilience/SubscriptionRetryHelperReconnectBackoffTests.cs</tests>
  internal TimeSpan NextReconnectDelay(DateTimeOffset now, SubscriptionResilienceOptions options) {
    lock (_reconnectGate) {
      var delay = _lastReconnectDelay is { } last && now - _healthySince < options.ReconnectBackoffResetAfter
        ? SubscriptionRetryHelper.CalculateNextDelay(last, options)
        : options.InitialRetryDelay;
      _lastReconnectDelay = delay;
      return delay;
    }
  }

  /// <summary>
  /// Increments the attempt count by one.
  /// </summary>
  public void IncrementAttempt() => AttemptCount++;

  /// <summary>
  /// Resets the attempt count to zero.
  /// </summary>
  public void ResetAttempts() => AttemptCount = 0;
}
