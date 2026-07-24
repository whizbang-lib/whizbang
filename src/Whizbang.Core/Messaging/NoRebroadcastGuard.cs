using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The outbox-enqueue boundary's hard check for the no-rebroadcast invariant. A fan-out child is
/// stamped with <see cref="EventFlags.NoRebroadcast"/> on its envelope; when processing that child
/// triggers a publish, the source envelope carries the flag and this guard drops the publish before it
/// reaches the outbox. Belt-and-suspenders on top of hop-based echo suppression — even a future receptor
/// or code path that explicitly re-publishes a fan-out child is stopped here.
/// </summary>
/// <docs>fundamentals/messaging/composite-events#no-rebroadcast</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/NoRebroadcastGuardTests.cs</tests>
public static class NoRebroadcastGuard {
  /// <summary>
  /// True when <paramref name="sourceEnvelope"/> is a no-rebroadcast message (carries
  /// <see cref="EventFlags.NoRebroadcast"/>) — meaning any publish it triggers must be suppressed at the
  /// outbox-enqueue boundary. False for a null source (a fresh local publish, not a re-broadcast).
  /// </summary>
  public static bool ShouldSuppress(IMessageEnvelope? sourceEnvelope) =>
    sourceEnvelope is not null && (sourceEnvelope.Flags & EventFlags.NoRebroadcast) != 0;
}
