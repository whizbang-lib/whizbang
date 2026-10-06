// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Data.EFCore.Postgres.Configuration;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for <see cref="IndexStatisticsMaintenanceStep"/>'s fleet claim: the claim key
/// names the model's schema (defaulting to public when the model maps no framework entity or maps
/// one without a schema) and the window the injected or system clock falls in. The claim store
/// refuses every claim, so no connection is ever opened.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/IndexStatisticsMaintenanceStep.cs</code-under-test>
[Category("Shard4")]
public class IndexStatisticsStepClaimTests {

  private const string UNUSED_CONNECTION = "Host=localhost;Database=never_opened";

  [Test]
  public async Task ClaimKey_NamesTheModelSchemaAndTheInjectedClocksWindowAsync() {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));

    var unmapped = await _claimKeyAsync(
      new BareContext(new DbContextOptionsBuilder<BareContext>().UseNpgsql(UNUSED_CONNECTION).Options), typeof(BareContext), clock);
    var scoped = await _claimKeyAsync(
      new ScopedContext(new DbContextOptionsBuilder<ScopedContext>().UseNpgsql(UNUSED_CONNECTION).Options), typeof(ScopedContext), clock);

    await Assert.That(unmapped).IsEqualTo(_expected("public", clock.GetUtcNow()))
      .Because("a model with no framework entity has no schema, so the step claims for public");
    await Assert.That(scoped).IsEqualTo(_expected("svc_stats", clock.GetUtcNow()))
      .Because("a model with a service schema claims, and later analyzes, that schema");
  }

  [Test]
  public async Task ClaimKey_WithoutAnInjectedClock_UsesTheSystemClockAsync() {
    var before = DateTimeOffset.UtcNow;
    var key = await _claimKeyAsync(
      new BareContext(new DbContextOptionsBuilder<BareContext>().UseNpgsql(UNUSED_CONNECTION).Options), typeof(BareContext), timeProvider: null);
    var after = DateTimeOffset.UtcNow;

    await Assert.That(new[] { _expected("public", before), _expected("public", after) }).Contains(key!);
  }

  [Test]
  public async Task NullContextType_ThrowsAsync() {
    await Assert.That(() => new IndexStatisticsMaintenanceStep(null!)).Throws<ArgumentNullException>();
  }

  private static string _expected(string schema, DateTimeOffset now) {
    var window = now.UtcTicks - (now.UtcTicks % IndexStatisticsMaintenanceStep.ClaimWindow.Ticks);
    return string.Create(CultureInfo.InvariantCulture, $"whizbang:index-statistics:{schema}:{window}");
  }

  private static async Task<string?> _claimKeyAsync(DbContext context, Type contextType, TimeProvider? timeProvider) {
    await using (context) {
      var claims = new RefusingClaims();
      var services = new ServiceCollection();
      services.AddSingleton(contextType, context);
      services.AddSingleton<IClaimedEmissionStore>(claims);
      await using var sp = services.BuildServiceProvider();
      await new IndexStatisticsMaintenanceStep(contextType, timeProvider: timeProvider).RunAsync(sp, CancellationToken.None);
      return claims.LastKey;
    }
  }

  /// <summary>Records the claim key it was asked for and refuses it, so the step stops there.</summary>
  private sealed class RefusingClaims : IClaimedEmissionStore {
    public string? LastKey { get; private set; }

    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken) {
      LastKey = claimKey;
      return Task.FromResult(false);
    }
  }

  /// <summary>A DbContext that maps none of the framework's entities.</summary>
  public sealed class BareContext(DbContextOptions<BareContext> options) : DbContext(options);

  /// <summary>A DbContext that maps the framework's entities under a service schema.</summary>
  public sealed class ScopedContext(DbContextOptions<ScopedContext> options) : DbContext(options) {
    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.ConfigureWhizbangInfrastructure();
      modelBuilder.HasDefaultSchema("svc_stats");
    }
  }
}
