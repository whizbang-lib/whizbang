namespace Whizbang.Core.Routing;

/// <summary>
/// Knobs for the non-count-based poison detector (topology arc phase 8.5). Bound from
/// <c>Whizbang:Routing:PoisonMessages</c> by <see cref="PoisonMessageOptionsConfigurationBinder"/>,
/// so the killswitch and both bounds are operator-reachable without a redeploy.
/// <para>
/// Why this exists: on SESSION-enabled entities a lock loss caused by connection death does NOT
/// increment the broker's delivery count (explicit abandon does; non-session lock loss does).
/// Command inboxes are session-enabled by default, so the broker's MaxDeliveryCount valve — and
/// every transport branch reading the same counter — is structurally unreachable under a
/// consumer-death storm. Those messages are hostage, not poison, and nothing else bounds the
/// loop. These options bound it on signals that DO survive redelivery: the broker's first-enqueue
/// timestamp (layer 1) and a durable redelivery-observation counter (layer 2).
/// </para>
/// </summary>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/PoisonMessageDetectorTests.cs</tests>
public sealed class PoisonMessageOptions {

#pragma warning disable CA1707 // Repo style: public const/static-readonly fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>
  /// Lock-renewal window assumed when no transport supplied one. Matches the Azure Service Bus
  /// transport's <c>MaxAutoLockRenewalDuration</c> default so the derived threshold is the same
  /// number whether or not the transport post-configured it.
  /// </summary>
  public static readonly TimeSpan DEFAULT_LOCK_RENEWAL_DURATION = TimeSpan.FromMinutes(5);

  /// <summary>
  /// Delivery-attempt count assumed when no transport supplied one. Matches both transports'
  /// <c>MaxDeliveryAttempts</c> default.
  /// </summary>
  public const int DEFAULT_MAX_DELIVERY_ATTEMPTS = 10;

  /// <summary>
  /// Documented lower bound on the derived age threshold. A short lock-renewal window multiplied
  /// by a small attempt cap can derive a threshold measured in seconds; quarantining on that
  /// would kill legitimately slow work. Thirty minutes is the floor: below it, "old" is not
  /// evidence of anything.
  /// </summary>
  public static readonly TimeSpan DEFAULT_AGE_THRESHOLD_FLOOR = TimeSpan.FromMinutes(30);

  /// <summary>
  /// Default bound on durable redelivery observations. Ten independent observations of the same
  /// message id — each one a delivery this service recorded and then failed to settle — is a loop,
  /// not a retry.
  /// </summary>
  public const int DEFAULT_MAX_DURABLE_OBSERVATIONS = 10;
#pragma warning restore CA1707

  /// <summary>
  /// Master killswitch. When <c>false</c> the detector proceeds on every message — EVERY layer,
  /// not just the newest — restoring pre-phase-8.5 behavior. Default <c>true</c>: an unreachable
  /// dead-letter valve is the defect this phase closes.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Explicit age threshold. <c>null</c> (the default) derives it from
  /// <see cref="LockRenewalDuration"/>, <see cref="MaxDeliveryAttempts"/> and
  /// <see cref="AgeThresholdFloor"/> — see <see cref="DeriveAgeThreshold"/>. Set this only to
  /// override a derivation that is wrong for a specific workload.
  /// </summary>
  public TimeSpan? AgeThreshold { get; set; }

  /// <summary>
  /// The transport's lock-renewal window. Post-configured from the transport's own options at
  /// registration (Azure Service Bus supplies <c>MaxAutoLockRenewalDuration</c>), so moving the
  /// transport knob moves the poison threshold with it. <c>null</c> uses
  /// <see cref="DEFAULT_LOCK_RENEWAL_DURATION"/>.
  /// </summary>
  public TimeSpan? LockRenewalDuration { get; set; }

  /// <summary>
  /// The transport's delivery-attempt cap. Post-configured from the transport's own options at
  /// registration. <c>null</c> uses <see cref="DEFAULT_MAX_DELIVERY_ATTEMPTS"/>.
  /// </summary>
  public int? MaxDeliveryAttempts { get; set; }

  /// <summary>Lower bound applied to the derived age threshold. See <see cref="DEFAULT_AGE_THRESHOLD_FLOOR"/>.</summary>
  public TimeSpan AgeThresholdFloor { get; set; } = DEFAULT_AGE_THRESHOLD_FLOOR;

  /// <summary>
  /// Durable redelivery observations tolerated before layer 2 quarantines. See
  /// <see cref="DEFAULT_MAX_DURABLE_OBSERVATIONS"/>.
  /// </summary>
  public int MaxDurableObservations { get; set; } = DEFAULT_MAX_DURABLE_OBSERVATIONS;

  /// <summary>
  /// The age threshold actually in force: <see cref="AgeThreshold"/> when set, otherwise the
  /// derivation over the lock/delivery options.
  /// </summary>
  public TimeSpan EffectiveAgeThreshold =>
    AgeThreshold ?? DeriveAgeThreshold(
      LockRenewalDuration ?? DEFAULT_LOCK_RENEWAL_DURATION,
      MaxDeliveryAttempts ?? DEFAULT_MAX_DELIVERY_ATTEMPTS,
      AgeThresholdFloor);

  /// <summary>
  /// Derives the age threshold as <c>max(floor, lockRenewalDuration x maxDeliveryAttempts)</c>.
  /// <para>
  /// The product is the longest window a message may legally occupy while still PROGRESSING: the
  /// transport renews a lock for at most <paramref name="lockRenewalDuration"/> per delivery and
  /// gives up after <paramref name="maxDeliveryAttempts"/> of them. A message older than that has
  /// consumed every legitimate retry the transport was configured to grant, so age is evidence —
  /// not a guess. The floor keeps a degenerate configuration (sub-second renewal, single attempt)
  /// from deriving a threshold that quarantines healthy traffic on arrival.
  /// </para>
  /// Non-positive inputs fall back to the floor; an overflowing product saturates at
  /// <see cref="TimeSpan.MaxValue"/> rather than throwing.
  /// </summary>
  /// <param name="lockRenewalDuration">The transport's per-delivery lock-renewal window.</param>
  /// <param name="maxDeliveryAttempts">The transport's delivery-attempt cap.</param>
  /// <param name="floor">Lower bound on the result.</param>
  /// <returns>The derived age threshold.</returns>
  public static TimeSpan DeriveAgeThreshold(TimeSpan lockRenewalDuration, int maxDeliveryAttempts, TimeSpan floor) {
    if (lockRenewalDuration <= TimeSpan.Zero || maxDeliveryAttempts <= 0) {
      return floor;
    }

    TimeSpan product;
    try {
      product = lockRenewalDuration * maxDeliveryAttempts;
    } catch (OverflowException) {
      return TimeSpan.MaxValue;
    }

    return product > floor ? product : floor;
  }
}
