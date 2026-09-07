#pragma warning disable CS0618, WHIZ400

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="MultiModelScopeHelper.GetQuery{T}"/>'s Split-mode branch — every test in
/// the sibling <see cref="MultiModelScopedAccessTests"/> suite uses a model with no
/// <see cref="SplitModeChangeTrackerHydrator"/> registration, so <c>GetQuery</c> always takes the
/// <c>AsNoTracking()</c> else-branch. None ever register a hydrator, so the tracking branch — the
/// one every real Split-mode model actually needs — has never run through this call path.
/// </summary>
/// <remarks>
/// <c>SplitModeChangeTrackerHydrator</c>'s registry is process-global — the same shared-static hazard
/// recorded for it elsewhere (<c>SplitModeChangeTrackerHydratorCoverageTests</c>). Tagged with the
/// shared <c>"EFCorePostgresTests"</c> <see cref="NotInParallelAttribute"/> key for the same reason.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/MultiModelScopedAccess.cs</code-under-test>
[Category("Shard1")]
[NotInParallel("EFCorePostgresTests")]
public class MultiModelScopedAccessCoverageTests {

  private sealed record _splitProbeModel { public string Value { get; init; } = ""; }

  private sealed class _probeDbContext(DbContextOptions<_probeDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<_splitProbeModel>>(entity => {
        entity.HasKey(e => e.Id);
        entity.OwnsOne(e => e.Data, data => data.WithOwner());
        entity.OwnsOne(e => e.Metadata, metadata => {
          metadata.WithOwner();
          metadata.Property(m => m.EventType).IsRequired();
          metadata.Property(m => m.EventId).IsRequired();
          metadata.Property(m => m.Timestamp).IsRequired();
        });
        entity.Property(e => e.Scope)
          .HasConversion(
            v => JsonSerializer.Serialize(v, JsonSerializerOptions.Default),
            v => JsonSerializer.Deserialize<PerspectiveScope>(v, JsonSerializerOptions.Default)!);
      });
    }
  }

  [After(Test)]
  public void ClearAfter() => SplitModeChangeTrackerHydrator.Clear();

  // SplitModeChangeTrackerHydrator's whole mechanism depends on ChangeTracker.Tracked actually
  // firing for the materialized row — which only happens for a TRACKING query, and only after
  // EnsureHooked has subscribed the handler. If GetQuery ever used AsNoTracking() for a model with a
  // registered hydrator (or forgot to call EnsureHooked), a multi-model query against a Split-mode
  // model would silently skip hydration: the physical-field shadow values would never reach Data,
  // and every consumer reading through this scoped-access path would see stale/missing data for
  // exactly the columns Split mode exists to serve.
  [Test]
  public async Task GetQuery_SplitModeHydratorRegistered_TracksAndFiresTheHydratorAsync() {
    var probeType = typeof(PerspectiveRow<_splitProbeModel>);
    var hydratorInvocations = 0;
    SplitModeChangeTrackerHydrator.Register(probeType, _ => hydratorInvocations++);

    var options = new DbContextOptionsBuilder<_probeDbContext>()
      .UseInMemoryDatabase($"multimodel-split-{Guid.NewGuid()}")
      .Options;
    await using var context = new _probeDbContext(options);

    var id = Guid.NewGuid();
    context.Add(new PerspectiveRow<_splitProbeModel> {
      Id = id,
      Data = new _splitProbeModel { Value = "seed" },
      Metadata = new PerspectiveMetadata { EventType = "Created", EventId = Guid.NewGuid().ToString(), Timestamp = DateTime.UtcNow },
      Scope = new PerspectiveScope(),
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1,
    });
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();  // detach so the read below is a fresh materialization, not a cache hit

    var query = MultiModelScopeHelper.GetQuery<_splitProbeModel>(context, null);
    var results = await query.ToListAsync();

    await Assert.That(results.Count).IsEqualTo(1)
      .Because("the seeded row must still come back — this test is about HOW it's queried, not whether it's found");
    await Assert.That(hydratorInvocations).IsEqualTo(1)
      .Because("the registered hydrator only ever runs off ChangeTracker.Tracked, which only fires for a "
             + "TRACKING query with EnsureHooked already subscribed — a single invocation proves both the "
             + "AsQueryable() (not AsNoTracking()) branch AND the EnsureHooked call actually ran");
    await Assert.That(context.ChangeTracker.Entries<PerspectiveRow<_splitProbeModel>>().Any()).IsTrue()
      .Because("the materialized row must actually be tracked, not just have triggered the event once and "
             + "then been forgotten");
  }
}
