using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// Decides whether an exhausted message is DROPPED at the dead-letter boundary instead of being
/// stored. Only framework/consumer control-plane traffic is dropped; domain messages are always
/// preserved.
/// </summary>
/// <remarks>
/// Storing control-plane traffic is a contradiction: checkpoints, manifests and re-delivery
/// requests are periodic and re-emitted, so a stored copy is already worthless when an operator
/// reads it. Worse, it is not inert — the recovery worker re-emits stored rows into the inbox, so a
/// burst of control-plane failures becomes a backlog that every later boot replays. Observed live:
/// tens of thousands of such rows per service, surviving repeated queue purges because they were
/// held in the dead-letter table rather than the queues being purged.
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/DeadLetterControlPlaneDropTests.cs</tests>
public static class DeadLetterDropPolicy {
  /// <summary>
  /// True when <paramref name="messageType"/> names control-plane traffic, which is dropped
  /// (logged + metered) rather than stored. An unidentifiable type fails SAFE — it is stored.
  /// </summary>
  public static bool ShouldDropInsteadOfStore(string messageType) =>
    ControlPlaneTypeRegistry.IsControlPlane(messageType);
}
