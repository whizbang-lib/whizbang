namespace Whizbang.Core.Diagnostics;

/// <summary>
/// Reports event types a service both consumes and raises — the shape that compounds per hop.
/// </summary>
/// <remarks>
/// <para>
/// When a consumer's aggregates re-raise event types they subscribe to, each hop stores the event
/// into its own event store, that store publishes onward, and the next consumer does the same. The
/// multiplier compounds rather than adds, so a modest per-service factor becomes an enormous system
/// total across a few hops.
/// </para>
/// <para>
/// Measured on one event type in a bulk-import workload: a producer emitted roughly four thousand
/// events; four consumers subsequently held about 280,000 rows of that same type between them —
/// around seventy times the source. Handler count was ONE on every consumer, so it was neither
/// multiple handlers nor duplicate delivery of a single publish.
/// </para>
/// <para>
/// Every throughput control in the framework — claim windows, outstanding budgets, publish
/// batching, concurrency governors — acts on the volume AFTER this multiplication and therefore
/// cannot reduce it. An operator tuning them has no way to learn that the load itself is the
/// problem, because nothing distinguishes an event this service ORIGINATED from one it re-raised in
/// response to consuming something.
/// </para>
/// <para>
/// The intersection of subscribed types and raised types is known at wire-up and costs nothing to
/// compute. Reporting it turns the cascade into a design decision made deliberately, rather than
/// one discovered by hand-joining event stores across services during an incident.
/// </para>
/// <para>
/// This is deliberately a REPORT, not a guard. Re-raising a consumed type is legitimate in some
/// designs; the framework's job is to make the consequence visible, not to forbid it.
/// </para>
/// </remarks>
/// <docs>operations/observability/re-emission-cascade</docs>
/// <tests>tests/Whizbang.Core.Tests/Diagnostics/ReEmissionCascadeReportTests.cs</tests>
public sealed class ReEmissionCascadeReport {

  /// <summary>Types this service both subscribes to and raises, ordered for a stable log line.</summary>
  public IReadOnlyList<string> ReEmittedTypes { get; }

  /// <summary>True when at least one type is both consumed and raised.</summary>
  public bool HasCascade => ReEmittedTypes.Count > 0;

  private ReEmissionCascadeReport(IReadOnlyList<string> reEmitted) => ReEmittedTypes = reEmitted;

  /// <summary>
  /// Computes the re-emission set from a service's registrations.
  /// </summary>
  /// <param name="subscribedTypes">Event types this service consumes.</param>
  /// <param name="raisedTypes">Event types this service's aggregates raise.</param>
  /// <returns>The report; empty when nothing is both consumed and raised.</returns>
  public static ReEmissionCascadeReport Analyze(
      IEnumerable<string> subscribedTypes, IEnumerable<string> raisedTypes) {
    // A null registry silently reporting "no cascade" is the worst outcome available: it is the
    // same answer a healthy service gives, so the failure would never be noticed.
    ArgumentNullException.ThrowIfNull(subscribedTypes);
    ArgumentNullException.ThrowIfNull(raisedTypes);

    var subscribed = new HashSet<string>(subscribedTypes, StringComparer.Ordinal);
    if (subscribed.Count == 0) {
      return new ReEmissionCascadeReport([]);
    }

    // Set semantics throughout: a type registered twice is a registration detail, not two cascades,
    // and double-counting would overstate the finding and erode trust in the number.
    var reEmitted = new HashSet<string>(StringComparer.Ordinal);
    foreach (var raised in raisedTypes) {
      if (subscribed.Contains(raised)) {
        reEmitted.Add(raised);
      }
    }

    // Ordered so the startup line is identical across identical deployments — an unstable order
    // defeats diffing it between services to find which one introduced a cascade.
    var ordered = reEmitted.ToArray();
    Array.Sort(ordered, StringComparer.Ordinal);
    return new ReEmissionCascadeReport(ordered);
  }
}
