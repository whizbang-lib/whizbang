// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Health;

namespace Whizbang.Core.Tests.Health;

/// <summary>
/// Covers the health DI wiring: <c>AddWhizbangManagedHealth</c> registers an aggregator over every
/// source added with <c>AddWhizbangHealthSource</c>, and applies the configured policy.
/// </summary>
public class WhizbangHealthDiTests {

  private sealed class MigratingSource : IWhizbangHealthSource {
    public string Component => "schema";
    public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken)
      => new(new ComponentHealth(ComponentState.Migrating));
  }

  [Test]
  public async Task AddWhizbangManagedHealth_AggregatesRegisteredSourcesAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangManagedHealth();
    services.AddWhizbangHealthSource<MigratingSource>();
    await using var provider = services.BuildServiceProvider();

    var aggregator = provider.GetRequiredService<WhizbangHealthAggregator>();
    var result = await aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);

    await Assert.That(result.Components.Count).IsEqualTo(1);
    await Assert.That(result.Status).IsEqualTo(HealthStatus.Degraded); // Migrating: visible-but-serving under the Lenient default
  }

  private sealed class UnansweredSource : IWhizbangHealthSource {
    private readonly TaskCompletionSource<ComponentHealth> _never = new();
    public string Component => "transport";
    public ValueTask<ComponentHealth> ReportAsync(CancellationToken cancellationToken) => new(_never.Task);
  }

  [Test]
  public async Task AddWhizbangManagedHealth_TimesSourcesOnTheRegisteredClockAsync() {
    // A host that registers its own TimeProvider runs the per-source deadline on it, so the deadline is armed on the
    // registered clock and elapses when that clock is advanced.
    var clock = new ArmCountingClock();
    var services = new ServiceCollection();
    services.AddSingleton<TimeProvider>(clock);
    services.AddWhizbangManagedHealth();
    services.AddWhizbangHealthSource<UnansweredSource>();
    await using var provider = services.BuildServiceProvider();

    var aggregator = provider.GetRequiredService<WhizbangHealthAggregator>();
    var evaluation = aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None).AsTask();
    await Assert.That(clock.Armed).IsEqualTo(1)
      .Because("a deadline armed on the system clock ignores the host's clock entirely");

    clock.Advance(provider.GetRequiredService<WhizbangHealthOptions>().SourceTimeout);
    var result = await evaluation;

    await Assert.That(result.Components.Single().State).IsEqualTo(ComponentState.Faulted);
  }

  private sealed class ArmCountingClock : FakeTimeProvider {
    public int Armed { get; private set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      Armed++;
      return base.CreateTimer(callback, state, dueTime, period);
    }
  }

  [Test]
  public async Task AddWhizbangHealthSource_SameSourceTwice_RegistersOnceAsync() {
    // A consumer with more than one worker pipeline (e.g. one per DbContext) calls the pipeline
    // registration N times, which historically stacked N identical source registrations — N
    // evaluations of the same gates per probe and duplicate component names downstream.
    var services = new ServiceCollection();
    services.AddWhizbangManagedHealth();
    services.AddWhizbangHealthSource<MigratingSource>();
    services.AddWhizbangHealthSource<MigratingSource>();
    await using var provider = services.BuildServiceProvider();

    var aggregator = provider.GetRequiredService<WhizbangHealthAggregator>();
    var result = await aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);

    await Assert.That(result.Components.Count).IsEqualTo(1);
  }

  [Test]
  public async Task AddWhizbangManagedHealth_AppliesPolicyConfigurationAsync() {
    var services = new ServiceCollection();
    services.AddWhizbangManagedHealth(o => o.Components["schema"] = HealthPolicy.Strict);
    services.AddWhizbangHealthSource<MigratingSource>();
    await using var provider = services.BuildServiceProvider();

    var aggregator = provider.GetRequiredService<WhizbangHealthAggregator>();
    var result = await aggregator.EvaluateAsync(HealthProbe.Readiness, CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy); // Strict: Migrating => not ready
  }
}
