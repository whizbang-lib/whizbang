// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Whizbang.Core.Capabilities;

/// <summary>
/// The <c>whizbang_capabilities</c> health entry, from the latest verdict: unhealthy while an explicitly
/// configured feature lacks its capability, degraded while a required capability warns or degrades, healthy
/// otherwise. Its data holds the verdict line of every required capability that is not satisfied.
/// </summary>
/// <docs>operations/infrastructure/capabilities#health</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityHealthCheckTests.cs</tests>
public sealed class CapabilityHealthCheck : IHealthCheck {
#pragma warning disable CA1707
  /// <summary>The health entry's name.</summary>
  public const string NAME = "whizbang_capabilities";
#pragma warning restore CA1707

  private readonly ICapabilityRegistry _registry;

  /// <summary>Creates the check over the registry whose verdict it reports.</summary>
  /// <param name="registry">The registry.</param>
  public CapabilityHealthCheck(ICapabilityRegistry registry) {
    ArgumentNullException.ThrowIfNull(registry);
    _registry = registry;
  }

  /// <inheritdoc />
  public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) {
    var entries = _registry.Verdict.Entries;
    if (entries.Count == 0) {
      return Task.FromResult(HealthCheckResult.Healthy("no capability is required"));
    }
    var unsatisfied = entries
      .Where(entry => entry.Outcome is not (CapabilityVerdictOutcome.Proceed or CapabilityVerdictOutcome.Use))
      .ToList();
    if (unsatisfied.Count == 0) {
      return Task.FromResult(HealthCheckResult.Healthy($"{entries.Count} required capability, every one ready"));
    }
    var data = new Dictionary<string, object> {
      ["capabilities"] = (IReadOnlyList<string>)[.. unsatisfied.Select(entry => entry.Line)],
    };
    var failing = unsatisfied.Count(entry => entry.Outcome == CapabilityVerdictOutcome.Fail);
    return Task.FromResult(failing > 0
      ? HealthCheckResult.Unhealthy($"{failing} explicitly configured feature lacks its capability", data: data)
      : HealthCheckResult.Degraded($"{unsatisfied.Count} required capability is not ready", data: data));
  }
}
