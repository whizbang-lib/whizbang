using Whizbang.Core.Minting;

namespace Whizbang.Core.Messaging;

/// <summary>
/// The stream-integrity messages that carry REPAIR rather than detection: the re-delivery request an
/// origin serves (<see cref="RequestRedeliveryCommand"/>) and the bundle a consumer folds in
/// (<see cref="RedeliveryComposite"/>). <see cref="IntegrityRepairMode.ReportOnly"/> is bilateral: a
/// service that opted down from repair takes no part in either direction, so the origin receptor
/// declines requests, the consumer's dispatch seam completes bundles without fanning them out, and the
/// maintenance sweep drops rows parked by retry backoff before their schedule brings them back.
/// Detection traffic (checkpoints, manifests, gap reports) is never in this set: report-only still reports.
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/RepairTrafficTests.cs</tests>
public static class RepairTraffic {
  /// <summary>
  /// Normalized (version-stripped) assembly-qualified names of the repair message types, in the form
  /// <see cref="EventTypeMatchingHelper.NormalizeTypeName"/> produces. A stored <c>message_type</c> may
  /// carry assembly version metadata or an envelope wrapper around the same name, so stores match by
  /// containment, not equality.
  /// </summary>
  public static IReadOnlyList<string> InboxMessageTypeNames { get; } = [
    EventTypeMatchingHelper.NormalizeTypeName(typeof(RequestRedeliveryCommand).AssemblyQualifiedName!),
    EventTypeMatchingHelper.NormalizeTypeName(typeof(RedeliveryComposite).AssemblyQualifiedName!),
  ];

  /// <summary>
  /// True only when the service has explicitly opted in to
  /// <see cref="IntegrityRepairMode.AutoRepairCapped"/>. Absent options read as the default, which is
  /// report-only.
  /// </summary>
  public static bool IsRepairEnabled(StreamIntegrityOptions? options)
    => options?.RepairMode == IntegrityRepairMode.AutoRepairCapped;
}
