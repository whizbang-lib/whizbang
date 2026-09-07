using Whizbang.Core.Minting;

namespace Whizbang.Core.Messaging;

/// <summary>
/// A stream-integrity feature that is off leaves nothing behind. Each control-plane message belongs to
/// one feature of <see cref="StreamIntegrityOptions"/>; when that feature is off, rows of that type
/// still pending in the inbox or outbox are work the service has decided not to do (minted before the
/// operator opted out, or delivered by a peer that does not know), and the maintenance sweep drops them.
/// The repair half is <see cref="RepairTraffic"/>; this type adds detection and reporting:
/// <list type="bullet">
/// <item><c>CheckpointsEnabled = false</c>: unpublished <see cref="IntegrityCheckpoint"/> rows in the outbox.</item>
/// <item><c>GapDetectionEnabled = false</c>: received <see cref="IntegrityCheckpoint"/> rows in the inbox (only gap detection consumes them).</item>
/// <item><c>AuditEnabled = false</c>: unsent <see cref="RequestIntegrityManifest"/> asks in the outbox and received
/// <see cref="IntegrityManifest"/> answers in the inbox. Requests FROM peers are still answered: a service that
/// does not audit can still be audited.</item>
/// <item><c>PublishReportEvents = false</c>: unpublished <see cref="IntegrityGapDetected"/>,
/// <see cref="IntegrityDivergenceDetected"/> and <see cref="PerspectiveCoverageGapDetected"/> reports in the outbox.</item>
/// </list>
/// Everything a feature that is ON produces is left alone, so a service with defaults sweeps only repair
/// traffic (report-only) and unpublished report events (publishing is opt-in).
/// </summary>
/// <docs>resilience/stream-integrity</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/IntegrityTrafficTests.cs</tests>
public static class IntegrityTraffic {
  private static readonly string _checkpoint = _name(typeof(IntegrityCheckpoint));
  private static readonly string _manifest = _name(typeof(IntegrityManifest));
  private static readonly string _manifestRequest = _name(typeof(RequestIntegrityManifest));
  private static readonly string[] _reports = [
    _name(typeof(IntegrityGapDetected)),
    _name(typeof(IntegrityDivergenceDetected)),
    _name(typeof(PerspectiveCoverageGapDetected)),
  ];
  private static readonly string[] _repairOutbox = [
    _name(typeof(RequestRedeliveryCommand)),
    _name(typeof(RedeliveryComposite)),
  ];

  /// <summary>
  /// Normalized type names of pending INBOX rows the maintenance sweep discards under
  /// <paramref name="options"/> (absent options read as the defaults). Empty when every feature that
  /// receives traffic is on.
  /// </summary>
  public static IReadOnlyList<string> InboxTypesToDiscard(StreamIntegrityOptions? options) {
    var list = new List<string>();
    if (!RepairTraffic.IsRepairEnabled(options)) {
      list.AddRange(RepairTraffic.InboxMessageTypeNames);
    }
    if (options is { GapDetectionEnabled: false }) {
      list.Add(_checkpoint);
    }
    if (options is { AuditEnabled: false }) {
      list.Add(_manifest);
    }
    return list;
  }

  /// <summary>
  /// Normalized type names of pending OUTBOX rows the maintenance sweep discards under
  /// <paramref name="options"/> (absent options read as the defaults): what this service minted for a
  /// feature that is now off and never published.
  /// </summary>
  public static IReadOnlyList<string> OutboxTypesToDiscard(StreamIntegrityOptions? options) {
    var list = new List<string>();
    if (!RepairTraffic.IsRepairEnabled(options)) {
      list.AddRange(_repairOutbox);
    }
    if (options is { CheckpointsEnabled: false }) {
      list.Add(_checkpoint);
    }
    if (options is { AuditEnabled: false }) {
      list.Add(_manifestRequest);
    }
    if (options is null || !options.PublishReportEvents) {
      list.AddRange(_reports);
    }
    return list;
  }

  private static string _name(Type type) => EventTypeMatchingHelper.NormalizeTypeName(type.AssemblyQualifiedName!);
}
