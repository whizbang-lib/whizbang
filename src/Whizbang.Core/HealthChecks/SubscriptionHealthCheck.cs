using Microsoft.Extensions.Diagnostics.HealthChecks;
using Whizbang.Core.Resilience;
using Whizbang.Core.Transports;

namespace Whizbang.Core.HealthChecks;

/// <summary>
/// Health check that reports the status of transport subscriptions.
/// </summary>
/// <remarks>
/// <para>
/// Returns <see cref="HealthStatus.Healthy"/> when all subscriptions are healthy.
/// Returns <see cref="HealthStatus.Degraded"/> when some subscriptions are unhealthy but at least one is healthy.
/// Returns <see cref="HealthStatus.Unhealthy"/> when no subscriptions are healthy.
/// </para>
/// </remarks>
/// <docs>messaging/transports/transport-consumer#subscription-resilience</docs>
/// <tests>tests/Whizbang.Core.Tests/HealthChecks/SubscriptionHealthCheckTests.cs</tests>
/// <remarks>
/// Initializes a new instance of <see cref="SubscriptionHealthCheck"/>.
/// </remarks>
/// <param name="states">The subscription states to monitor.</param>
/// <exception cref="ArgumentNullException">Thrown when states is null.</exception>
public class SubscriptionHealthCheck(IReadOnlyDictionary<TransportDestination, SubscriptionState> states) : IHealthCheck {
  private readonly IReadOnlyDictionary<TransportDestination, SubscriptionState> _states = states ?? throw new ArgumentNullException(nameof(states));

  /// <inheritdoc />
  public Task<HealthCheckResult> CheckHealthAsync(
    HealthCheckContext context,
    CancellationToken cancellationToken = default
  ) {
    if (_states.Count == 0) {
      return Task.FromResult(HealthCheckResult.Healthy("No subscriptions configured"));
    }

    var healthyCount = _states.Values.Count(s => s.Status == SubscriptionStatus.Healthy);
    var failedCount = _states.Values.Count(s => s.Status == SubscriptionStatus.Failed);
    var recoveringCount = _states.Values.Count(s => s.Status == SubscriptionStatus.Recovering);
    var pendingCount = _states.Values.Count(s => s.Status == SubscriptionStatus.Pending);
    var totalCount = _states.Count;

    var data = new Dictionary<string, object>();

    // Add failed destinations to data
    var failedDestinations = _states
      .Where(kvp => kvp.Value.Status == SubscriptionStatus.Failed)
      .Select(kvp => kvp.Key.Address)
      .ToList();

    if (failedDestinations.Count > 0) {
      data["failed_destinations"] = (IReadOnlyList<string>)failedDestinations;
    }

    // Add recovering destinations to data
    var recoveringDestinations = _states
      .Where(kvp => kvp.Value.Status == SubscriptionStatus.Recovering)
      .Select(kvp => kvp.Key.Address)
      .ToList();

    if (recoveringDestinations.Count > 0) {
      data["recovering_destinations"] = (IReadOnlyList<string>)recoveringDestinations;
    }

    // All healthy
    if (healthyCount == totalCount) {
      return Task.FromResult(HealthCheckResult.Healthy(
        $"All subscriptions healthy: {healthyCount}/{totalCount}",
        data
      ));
    }

    // All failed
    if (failedCount == totalCount) {
      return Task.FromResult(HealthCheckResult.Unhealthy(
        $"All subscriptions failed: {healthyCount}/{totalCount}",
        data: data
      ));
    }

    // Mixed status - degraded
    var description = $"Some subscriptions unhealthy: {healthyCount}/{totalCount} healthy";
    if (recoveringCount > 0) {
      description += $", {recoveringCount} recovering";
    }
    if (failedCount > 0) {
      description += $", {failedCount} failed";
    }
    if (pendingCount > 0) {
      description += $", {pendingCount} pending";
    }

    return Task.FromResult(HealthCheckResult.Degraded(description, data: data));
  }
}
