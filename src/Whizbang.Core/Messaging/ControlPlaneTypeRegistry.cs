using System.Collections.Concurrent;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Name-keyed view of the <see cref="IControlPlaneMessage"/> marker, for the seams that hold only a
/// persisted message-type string and cannot test assignability — chiefly the dead-letter decision
/// in the inbox/outbox workers.
/// </summary>
/// <remarks>
/// <para>
/// Durably dead-lettering control-plane traffic is a contradiction. Checkpoints, manifests and
/// re-delivery requests are periodic and re-emitted by design, so a stored copy is already
/// worthless when an operator reads it — and it is not inert: the recovery worker feeds stored
/// rows back into the inbox, so a burst of failures becomes a backlog that every subsequent boot
/// re-runs. Observed live: tens of thousands of control-plane rows per service, surviving repeated
/// queue purges because they were hiding in the dead-letter table.
/// </para>
/// <para>
/// The framework registers its own control-plane types below; consumers may register their own
/// infrastructure signals, matching the opt-in-per-type contract the marker interface already
/// documents. Matching normalizes assembly decoration, because persisted rows carry fully
/// assembly-qualified names while registrations use the short wire form.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ControlPlaneTypeRegistryTests.cs</tests>
public static class ControlPlaneTypeRegistry {
  private static readonly ConcurrentDictionary<string, bool> _names = new(StringComparer.Ordinal);

  // Explicit static constructor (not [ModuleInitializer], which is not for libraries): it removes
  // beforefieldinit, so the framework's own types are guaranteed registered before any read.
  static ControlPlaneTypeRegistry() {
    Register(typeof(IntegrityCheckpoint));
    Register(typeof(IntegrityGapDetected));
    Register(typeof(IntegrityManifest));
    Register(typeof(RequestIntegrityManifest));
    Register(typeof(IntegrityDivergenceDetected));
    Register(typeof(PerspectiveCoverageGapDetected));
    Register(typeof(RequestRedeliveryCommand));
    // The BUNDLE the redelivery control plane ships back, not just the request for it. Missing it
    // meant a failed repair bundle was dead-lettered like domain traffic instead of dropped —
    // which fed the recovery ladder and produced unbounded churn.
    Register(typeof(RedeliveryComposite));
    Register(typeof(Whizbang.Core.SystemEvents.AuditEventsComposite));
    Register(typeof(CoalescedEventsComposite));
    Register(typeof(Whizbang.Core.Commands.System.RebuildPerspectiveCommand));
  }

  /// <summary>Registers a message type name as control-plane. Idempotent.</summary>
  /// <param name="messageTypeName">Wire form ("Type, Assembly") or a fully assembly-qualified name.</param>
  public static void Register(string messageTypeName) {
    ArgumentException.ThrowIfNullOrEmpty(messageTypeName);
    _names[EventTypeMatchingHelper.NormalizeTypeName(messageTypeName)] = true;
  }

  /// <summary>Registers a control-plane message type. Idempotent.</summary>
  /// <param name="type">The message type; must implement <see cref="IControlPlaneMessage"/>.</param>
  public static void Register(Type type) {
    ArgumentNullException.ThrowIfNull(type);
    Register(TypeNameFormatter.Format(type));
  }

  /// <summary>
  /// Whether <paramref name="messageTypeName"/> names a control-plane message. An empty or unknown
  /// name is NOT control-plane — an unreadable type fails safe by keeping the row.
  /// </summary>
  public static bool IsControlPlane(string messageTypeName) =>
    !string.IsNullOrEmpty(messageTypeName)
      && _names.ContainsKey(EventTypeMatchingHelper.NormalizeTypeName(messageTypeName));

}
