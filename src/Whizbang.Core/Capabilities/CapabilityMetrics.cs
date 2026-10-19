// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Capabilities;

/// <summary>
/// The <c>Whizbang.Capabilities</c> meter: <c>whizbang.capability.state</c>, an observable gauge with one
/// series per capability and scope, value 1, tagged <c>provider</c>, <c>capability</c>, <c>scope</c> and
/// <c>state</c>, for every provider alike.
/// </summary>
/// <remarks>
/// Value 1 on a state tag lets a dashboard count or alert by state ("any capability NotPermitted
/// anywhere") without decoding a number. Each collection reads the registry's latest answer.
/// </remarks>
/// <docs>operations/observability/metrics#capabilities</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityMetricsTests.cs</tests>
public sealed class CapabilityMetrics {
#pragma warning disable CA1707
  /// <summary>The OpenTelemetry meter name for this metrics group.</summary>
  public const string METER_NAME = "Whizbang.Capabilities";
#pragma warning restore CA1707

  /// <summary>Creates the gauge over the registry it reports.</summary>
  /// <param name="whizbangMetrics">The shared meter factory owner.</param>
  /// <param name="registry">The answers the gauge reports.</param>
  public CapabilityMetrics(WhizbangMetrics whizbangMetrics, ICapabilityRegistry registry) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    ArgumentNullException.ThrowIfNull(registry);
    var meter = whizbangMetrics.MeterFactory.Create(METER_NAME);
    CapabilityState = meter.CreateObservableGauge(
      "whizbang.capability.state",
      observeValues: () => registry.Statuses.Select(status => new Measurement<int>(1,
        new KeyValuePair<string, object?>("provider", status.Descriptor.Id.Provider),
        new KeyValuePair<string, object?>("capability", status.Descriptor.Id.Name),
        new KeyValuePair<string, object?>("scope", status.Scope),
        new KeyValuePair<string, object?>("state", status.State.ToString()))),
      description: "One series per capability and scope, value 1, tagged with the capability's state");
  }

  /// <summary>The <c>whizbang.capability.state</c> gauge.</summary>
  public ObservableGauge<int> CapabilityState { get; }
}
