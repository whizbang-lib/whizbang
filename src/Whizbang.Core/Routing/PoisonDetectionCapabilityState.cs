using System.Collections.Concurrent;

namespace Whizbang.Core.Routing;

/// <summary>
/// One receive surface (transport + entity) that cannot supply a trustworthy first-enqueue
/// timestamp, so age-based quarantine (layer 1) is inert on it and detection falls back to
/// durable observation counting (layer 2).
/// </summary>
/// <param name="Transport">Transport identifier, e.g. <c>azure-service-bus</c> / <c>rabbitmq</c>.</param>
/// <param name="Entity">The broker entity (topic/subscription/queue) the surface consumes.</param>
/// <param name="Detail">Human-readable diagnosis for logs and health detail.</param>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/PoisonMessageDetectorTests.cs</tests>
public sealed record PoisonDetectionCapabilityFinding(
  string Transport,
  string Entity,
  string Detail
);

/// <summary>
/// Thread-safe record of which receive surfaces can supply a trustworthy broker first-enqueue
/// timestamp (topology arc phase 8.5). Azure Service Bus always stamps one; RabbitMQ's is
/// publisher-set and therefore optional — a message published by a non-Whizbang producer, or by
/// an older build, arrives without it.
/// <para>
/// The state exists so the fallback is VISIBLE. The valve this phase replaces failed silently for
/// its whole life; a second silently-inert valve would be the same defect wearing a new name.
/// <see cref="Health.PoisonDetectionHealthSource"/> surfaces the degraded set as a Degraded
/// component, and <see cref="PoisonMessageDetector"/> logs a one-shot warning per surface.
/// </para>
/// </summary>
/// <docs>fundamentals/dispatcher/routing#poison-messages</docs>
/// <tests>tests/Whizbang.Core.Tests/Routing/PoisonMessageDetectorTests.cs</tests>
public sealed class PoisonDetectionCapabilityState {
  private readonly ConcurrentDictionary<(string Transport, string Entity), bool> _surfaces = new();

  /// <summary>
  /// Records whether <paramref name="transport"/>/<paramref name="entity"/> can supply a
  /// trustworthy age. Idempotent per surface; returns <c>true</c> only on the transition INTO the
  /// degraded state, so callers can log once per surface instead of once per message.
  /// </summary>
  /// <param name="transport">Transport identifier.</param>
  /// <param name="entity">Broker entity the surface consumes.</param>
  /// <param name="canSupplyTrustworthyAge">Whether a broker first-enqueue timestamp is available.</param>
  /// <returns><c>true</c> when this call newly degraded the surface.</returns>
  /// <exception cref="ArgumentNullException">Thrown when transport or entity is null.</exception>
  public bool ReportAgeCapability(string transport, string entity, bool canSupplyTrustworthyAge) {
    ArgumentNullException.ThrowIfNull(transport);
    ArgumentNullException.ThrowIfNull(entity);

    var key = (transport, entity);
    var newlyDegraded = false;
    _surfaces.AddOrUpdate(
      key,
      addValueFactory: _ => {
        newlyDegraded = !canSupplyTrustworthyAge;
        return canSupplyTrustworthyAge;
      },
      updateValueFactory: (_, existing) => {
        newlyDegraded = existing && !canSupplyTrustworthyAge;
        return canSupplyTrustworthyAge;
      });
    return newlyDegraded;
  }

  /// <summary>True while at least one receive surface cannot supply a trustworthy age.</summary>
  public bool HasDegradedSurface => _surfaces.Any(static entry => !entry.Value);

  /// <summary>Snapshot of every currently-degraded surface, ordered for stable health detail.</summary>
  public IReadOnlyList<PoisonDetectionCapabilityFinding> DegradedSurfaces => [.. _surfaces
    .Where(static entry => !entry.Value)
    .OrderBy(static entry => entry.Key.Transport, StringComparer.Ordinal)
    .ThenBy(static entry => entry.Key.Entity, StringComparer.Ordinal)
    .Select(static entry => new PoisonDetectionCapabilityFinding(
      entry.Key.Transport,
      entry.Key.Entity,
      $"'{entry.Key.Transport}' cannot supply a broker first-enqueue timestamp for '{entry.Key.Entity}'"))];
}
