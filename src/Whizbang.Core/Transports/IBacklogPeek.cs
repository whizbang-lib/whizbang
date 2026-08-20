using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Whizbang.Core.Transports;

/// <summary>
/// One entity's backlog observation (transport traffic classes, topology arc phase 10): how deep
/// it is, and how old its oldest message is.
/// </summary>
/// <remarks>
/// AGE is the load-bearing field, and depth alone is why this record needs it. During the incident
/// the arc was written for, subscription backlogs of tens of thousands of messages were HOSTAGE,
/// not poison: the moment the redelivery churn stopped they drained to zero untouched. A depth
/// alarm would have fired on both that and a genuinely stuck consumer; an age alarm separates them.
/// </remarks>
/// <param name="Entity">The subscription / queue name — what the health detail must name.</param>
/// <param name="Depth">Messages waiting on the entity.</param>
/// <param name="OldestAge">Age of the oldest waiting message, or null when the transport cannot
/// supply one (capability honesty — reported, never silently treated as zero).</param>
/// <docs>operations/observability/managed-resource-health#backlog-age</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/BacklogAgeDutyTests.cs</tests>
public readonly record struct BacklogSample(string Entity, long Depth, TimeSpan? OldestAge) {
  /// <summary>The short transport tag (<c>asb</c>, <c>rabbitmq</c>); defaults to unknown.</summary>
  public string Transport { get; init; } = "unknown";

  /// <summary>The TransportNamespace the entity lives in; defaults to <c>default</c>.</summary>
  public string TransportNamespace { get; init; } = TransportNamespaces.DEFAULT_KEY;

  /// <summary>The traffic class the entity carries; defaults to <c>domain</c> (unclassified).</summary>
  public string TrafficClass { get; init; } = TrafficClasses.DOMAIN;
}

/// <summary>
/// The traffic-class vocabulary used as an observability dimension. A class names WHY traffic
/// exists, which is the axis an operator reasons about when one class starves another.
/// </summary>
/// <docs>operations/observability/managed-resource-health#backlog-age</docs>
public static class TrafficClasses {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>Unclassified (application) traffic — everything with no routing tag bound.</summary>
  public const string DOMAIN = "domain";
#pragma warning restore CA1707
}

/// <summary>
/// A transport's admin-plane backlog peek (topology arc phase 10). Optional and injected, exactly
/// like <c>IMessageDiscardPolicy</c> and <c>IPoisonMessageDetector</c>: nothing is added to
/// <c>ITransport</c>, so a custom transport or a test double that never provides one simply
/// contributes no samples.
/// </summary>
/// <remarks>
/// Implementations MUST stay cheap — one management operation per entity per duty tick. The duty
/// exists because the transport's idle machinery once consumed a whole namespace quota unobserved;
/// an expensive observer would be the same bug wearing a different hat.
/// </remarks>
/// <docs>operations/observability/managed-resource-health#backlog-age</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/BacklogAgeDutyTests.cs</tests>
public interface IBacklogPeek {
  /// <summary>The short transport tag this peek reports under (<c>asb</c>, <c>rabbitmq</c>).</summary>
  string TransportName { get; }

  /// <summary>
  /// Samples every entity this transport currently consumes from.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>One sample per entity; empty when nothing is subscribed yet.</returns>
  Task<IReadOnlyList<BacklogSample>> PeekAsync(CancellationToken cancellationToken);
}
