using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Whizbang.Core.Observability;

/// <summary>
/// One entity whose backlog is older than the configured threshold.
/// </summary>
/// <param name="Entity">The subscription / queue name.</param>
/// <param name="Transport">The short transport tag.</param>
/// <param name="TransportNamespace">The TransportNamespace the entity lives in.</param>
/// <param name="TrafficClass">The traffic class the entity carries.</param>
/// <param name="Depth">Messages waiting.</param>
/// <param name="OldestAge">Age of the oldest waiting message.</param>
/// <docs>operations/observability/managed-resource-health#backlog-age</docs>
public sealed record BacklogAgeFinding(
  string Entity,
  string Transport,
  string TransportNamespace,
  string TrafficClass,
  long Depth,
  TimeSpan OldestAge);

/// <summary>
/// The shared state the backlog-age duty writes and
/// <see cref="Health.BacklogAgeHealthSource"/> reads (topology arc phase 10). Shaped on
/// <c>TopologyDriftState</c> / <c>PoisonDetectionCapabilityState</c>: a mutable, lock-free object
/// the periodic producer updates and the health surface projects, so the health source itself does
/// no work and no I/O.
/// </summary>
/// <remarks>
/// Findings are REPLACED wholesale on every tick rather than accumulated. A backlog signal that
/// only ever goes up is indistinguishable from a stuck one after the first occurrence; this one
/// clears itself the moment the entity drains, which is what makes it a state and not an event log.
/// </remarks>
/// <docs>operations/observability/managed-resource-health#backlog-age</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/BacklogAgeDutyTests.cs:PeekOnce_EmptyEntity_ClearsAPreviousFindingAsync</tests>
public sealed class BacklogAgeState {
  private volatile IReadOnlyList<BacklogAgeFinding> _findings = [];
  private readonly ConcurrentDictionary<string, bool> _unknownAgeSurfaces = new(StringComparer.Ordinal);

  /// <summary>The entities currently over the age threshold; empty when everything is healthy.</summary>
  public IReadOnlyList<BacklogAgeFinding> AgedBacklogs => _findings;

  /// <summary>True when any entity is currently over the age threshold.</summary>
  public bool HasAgedBacklog => _findings.Count > 0;

  /// <summary>
  /// True when at least one surface could not supply an oldest-enqueue age. Capability honesty:
  /// age-based backlog detection needs a broker-supplied timestamp, and a transport that has none
  /// must say so rather than go silently inert (the phase-8.5 rule, applied to this signal).
  /// </summary>
  public bool HasUnknownAgeSurface => !_unknownAgeSurfaces.IsEmpty;

  /// <summary>The surfaces that could not supply an age, ordinally sorted.</summary>
  public IReadOnlyList<string> UnknownAgeSurfaces =>
    [.. _unknownAgeSurfaces.Keys.OrderBy(k => k, StringComparer.Ordinal)];

  /// <summary>
  /// Replaces the current findings with <paramref name="findings"/> — one whole tick's answer.
  /// </summary>
  /// <param name="findings">The entities over threshold this tick.</param>
  /// <exception cref="ArgumentNullException">Thrown when findings is null.</exception>
  public void Replace(IReadOnlyList<BacklogAgeFinding> findings) {
    ArgumentNullException.ThrowIfNull(findings);
    _findings = findings;
  }

  /// <summary>
  /// Records that <paramref name="entity"/> on <paramref name="transport"/> could not supply an
  /// oldest-enqueue age. Idempotent.
  /// </summary>
  /// <param name="transport">The short transport tag.</param>
  /// <param name="entity">The entity name.</param>
  public void ReportUnknownAge(string transport, string entity) {
    ArgumentException.ThrowIfNullOrEmpty(transport);
    ArgumentException.ThrowIfNullOrEmpty(entity);
    _unknownAgeSurfaces[string.Create(CultureInfo.InvariantCulture, $"{transport}/{entity}")] = true;
  }
}
