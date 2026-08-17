using System.Collections.Concurrent;

namespace Whizbang.Transports.AzureServiceBus;

/// <summary>
/// Rotates busy sessions ahead of the SDK's lock-renewal cliff. The processor renews a
/// session's lock only for <c>MaxAutoLockRenewalDuration</c>, measured from session ACCEPT —
/// not per message. Under a deep backlog a session never goes idle, so every busy session
/// silently outlives its renewal window: the broker lock lapses mid-stream, every subsequent
/// completion throws <c>SessionLockLost</c>, messages redeliver until they dead-letter, and
/// sessions accepted together (a deploy) all hit the cliff in the same instant — observed live
/// as a fleet applying ~300 events/hour against tens of thousands queued.
///
/// <para>The governor tracks each session's continuous occupancy and flags it for a voluntary
/// <c>ReleaseSession()</c> once occupancy reaches <see cref="OccupancyBudget"/> — the renewal
/// window minus a safety margin that covers the in-flight message's processing and completion.
/// The processor then re-accepts the session (or another) under a FRESH lock; FIFO within the
/// session is preserved across the rotation, and completion always happens under a lock that
/// is still being renewed.</para>
/// </summary>
/// <param name="maxAutoLockRenewalDuration">The processor's renewal window. Infinite disables rotation.</param>
/// <docs>transports/azure-service-bus</docs>
/// <tests>tests/Whizbang.Transports.AzureServiceBus.Tests/SessionOccupancyGovernorTests.cs</tests>
public sealed class SessionOccupancyGovernor(TimeSpan maxAutoLockRenewalDuration) {
  private readonly ConcurrentDictionary<string, DateTimeOffset> _occupiedSince = new();

  /// <summary>
  /// How long a session may stay continuously occupied before rotation. The margin is a
  /// quarter of the renewal window, capped at 30 seconds — proportionally small for normal
  /// windows, and still leaving most of a short window usable instead of thrashing accepts.
  /// </summary>
  public TimeSpan OccupancyBudget { get; } =
    maxAutoLockRenewalDuration == System.Threading.Timeout.InfiniteTimeSpan
      ? System.Threading.Timeout.InfiniteTimeSpan
      : maxAutoLockRenewalDuration - _margin(maxAutoLockRenewalDuration);

  private static TimeSpan _margin(TimeSpan window) {
    var quarter = TimeSpan.FromTicks(window.Ticks / 4);
    return quarter < TimeSpan.FromSeconds(30) ? quarter : TimeSpan.FromSeconds(30);
  }

  /// <summary>Stamps the start of this session's continuous occupancy (first message after accept).</summary>
  public void RecordMessage(string sessionKey, DateTimeOffset now) =>
    _occupiedSince.TryAdd(sessionKey, now);

  /// <summary>
  /// True when the session's continuous occupancy has reached the budget — the completion-safe
  /// moment to hand the lock back before the SDK stops renewing it.
  /// </summary>
  public bool ShouldRelease(string sessionKey, DateTimeOffset now) =>
    OccupancyBudget != System.Threading.Timeout.InfiniteTimeSpan
    && _occupiedSince.TryGetValue(sessionKey, out var since)
    && now - since >= OccupancyBudget;

  /// <summary>
  /// Clears the session's occupancy clock after a release. Re-accepting takes a fresh broker
  /// lock, so the next message starts a fresh clock.
  /// </summary>
  public void OnReleased(string sessionKey) => _occupiedSince.TryRemove(sessionKey, out _);
}
