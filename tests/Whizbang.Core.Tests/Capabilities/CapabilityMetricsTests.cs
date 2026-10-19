// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Observability;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Capabilities;

/// <summary>
/// <c>whizbang.capability.state</c> on the <c>Whizbang.Capabilities</c> meter: one series per capability
/// and scope, value 1, tagged provider, capability, scope and state.
/// </summary>
/// <docs>operations/observability/metrics#capabilities</docs>
public class CapabilityMetricsTests {
  private static readonly string[] _series = [
    "postgres/vector/orders-db/Ready",
    "postgres/pg_squeeze/orders-db/NotPermitted",
    "broker/delayed_exchange/vhost-a/Missing",
  ];

  private sealed class FixedRegistry(IReadOnlyList<CapabilityStatus> statuses) : ICapabilityRegistry {
    public IReadOnlyList<ICapabilityProvider> Providers => [];
    public IReadOnlyList<CapabilityStatus> Statuses { get; set; } = statuses;
    public CapabilityState GetState(CapabilityId id, string scope) => CapabilityState.Unknown;
    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  }

  private static WhizbangMetrics _whizbangMetrics(ServiceProvider provider) =>
    new(provider.GetRequiredService<IMeterFactory>());

  [Test]
  public async Task Gauge_ReportsOneSeriesPerCapabilityAndScope_WithEveryTagAsync() {
    using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var registry = new FixedRegistry([
      new(FakeCapabilityProvider.Describe("postgres", "vector"), "orders-db", CapabilityState.Ready, "0.8.0", null, null),
      new(FakeCapabilityProvider.Describe("postgres", "pg_squeeze"), "orders-db", CapabilityState.NotPermitted, null, null, null),
      new(FakeCapabilityProvider.Describe("broker", "delayed_exchange"), "vhost-a", CapabilityState.Missing, null, null, null),
    ]);
    var metrics = new CapabilityMetrics(_whizbangMetrics(provider), registry);

    var series = GaugeRecorder.Collect(metrics.CapabilityState);

    await Assert.That(series.All(s => s.Value == 1)).IsTrue();
    await Assert.That(series.Select(s => $"{s.Tags["provider"]}/{s.Tags["capability"]}/{s.Tags["scope"]}/{s.Tags["state"]}").ToArray())
      .IsEquivalentTo(_series);
  }

  [Test]
  public async Task Gauge_ReadsTheLatestAnswerAtEachCollectionAsync() {
    using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var descriptor = FakeCapabilityProvider.Describe("postgres", "vector");
    var registry = new FixedRegistry([new(descriptor, "orders-db", CapabilityState.Unknown, null, null, "not probed yet")]);
    var metrics = new CapabilityMetrics(_whizbangMetrics(provider), registry);
    registry.Statuses = [new(descriptor, "orders-db", CapabilityState.Ready, "0.8.0", null, null)];

    var series = GaugeRecorder.Collect(metrics.CapabilityState);

    await Assert.That((string?)series.Single().Tags["state"]).IsEqualTo("Ready");
  }

  [Test]
  public async Task Gauge_IsNamedOnTheWhizbangCapabilitiesMeter_InTheTurnkeyListAsync() {
    using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
    var metrics = new CapabilityMetrics(_whizbangMetrics(provider), new FixedRegistry([]));

    await Assert.That(metrics.CapabilityState.Name).IsEqualTo("whizbang.capability.state");
    await Assert.That(metrics.CapabilityState.Meter.Name).IsEqualTo("Whizbang.Capabilities");
    await Assert.That(WhizbangMeters.All.Count(n => n == metrics.CapabilityState.Meter.Name)).IsEqualTo(1);
  }
}
