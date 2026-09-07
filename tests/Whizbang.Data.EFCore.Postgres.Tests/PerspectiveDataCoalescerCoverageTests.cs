using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="PerspectiveDataCoalescer.HasCoalescer"/> and the idempotency guard in
/// <see cref="PerspectiveDataCoalescer.EnsureHooked"/>. No Postgres container needed — both target
/// paths are pure in-process bookkeeping (a <c>ConcurrentDictionary</c> lookup and a
/// <c>ConditionalWeakTable</c> guard); the second test tracks an entity through EF Core's InMemory
/// provider purely to observe how many times <c>ChangeTracker.Tracked</c> actually fired.
/// <para>
/// <c>_coalescers</c> is process-global — the same shared-static hazard already recorded for
/// <c>SplitModeChangeTrackerHydrator</c> in <c>SplitModeChangeTrackerHydratorCoverageTests</c> and for
/// <c>IntegrityManifestReceptors._pagesFollowed</c> in <c>scratchpad/residue.md</c>. Tagged with the
/// shared <c>"EFCorePostgresTests"</c> <see cref="NotInParallelAttribute"/> key — the same one
/// <c>OrderSchemaEvolutionComplexTypeTests</c> carries while it registers/clears this exact registry
/// against a real container — so this file's <c>Register</c>/<c>Clear</c> calls can never interleave
/// with that suite's.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PerspectiveDataCoalescer.cs</code-under-test>
[Category("Shard1")]
[NotInParallel("EFCorePostgresTests")]
public class PerspectiveDataCoalescerCoverageTests {

  private sealed class _registeredProbeModel;
  private sealed class _neverRegisteredProbeModel;
  private sealed class _hookProbeEntity {
    public Guid Id { get; set; }
  }

  private sealed class _hookProbeDbContext(DbContextOptions<_hookProbeDbContext> options) : DbContext(options) {
    public DbSet<_hookProbeEntity> Probes => Set<_hookProbeEntity>();
  }

  [After(Test)]
  public void ClearAfter() => PerspectiveDataCoalescer.Clear();  // global registry — reset so tests stay independent

  // HasCoalescer is the query surface a caller (or future generated registration code, mirroring
  // SplitModeChangeTrackerHydrator.HasHydrator's role for scoped-access tracking decisions) uses to
  // ask "would Coalesce actually do anything for this model type?" without paying for a Coalesce
  // call. If it always answered false, a caller gating on it would believe no model ever needs
  // coalescing and skip work that was actually necessary — or if it always answered true, would do
  // unnecessary tracked-context bookkeeping for models with no null-collection risk at all.
  [Test]
  public async Task HasCoalescer_RegisteredType_ReturnsTrueAsync() {
    var probeType = typeof(_registeredProbeModel);
    PerspectiveDataCoalescer.Register(probeType, static _ => { });

    await Assert.That(PerspectiveDataCoalescer.HasCoalescer(probeType)).IsTrue()
      .Because("a type that was just registered must be reported as having a coalescer");
  }

  [Test]
  public async Task HasCoalescer_UnregisteredType_ReturnsFalseAsync() {
    await Assert.That(PerspectiveDataCoalescer.HasCoalescer(typeof(_neverRegisteredProbeModel))).IsFalse()
      .Because("a type nothing ever registered must not be reported as coalesced — a false positive here "
             + "would make a caller skip a real null-coalescing pass it still needs");
  }

  // EnsureHooked's guard is what keeps the ChangeTracker.Tracked subscription to exactly one per
  // DbContext instance. If a repeated EnsureHooked call (every single-row read seam calls it) ever
  // re-subscribed instead of returning early, every tracked entity's coalescer would run once per
  // extra subscription — for a coalescer that mutates a collection in place (turning null into []),
  // running it N times is harmless, but the doubled ChangeTracker.Tracked delegate chain would still
  // silently multiply the cost of every future tracked read for the life of the context, and a
  // future coalescer that is NOT idempotent to re-invocation would corrupt data on the second pass.
  [Test]
  public async Task EnsureHooked_CalledTwiceOnSameContext_SubscribesOnlyOnceAsync() {
    var probeType = typeof(_hookProbeEntity);
    var invocationCount = 0;
    PerspectiveDataCoalescer.Register(probeType, _ => invocationCount++);

    var options = new DbContextOptionsBuilder<_hookProbeDbContext>()
      .UseInMemoryDatabase($"coalescer-hook-{Guid.NewGuid()}")
      .Options;
    await using var context = new _hookProbeDbContext(options);

    PerspectiveDataCoalescer.EnsureHooked(context);
    PerspectiveDataCoalescer.EnsureHooked(context);  // second call must be a no-op

    context.Add(new _hookProbeEntity { Id = Guid.NewGuid() });

    await Assert.That(invocationCount).IsEqualTo(1)
      .Because("a second EnsureHooked call on the same context must not add a second Tracked subscription — "
             + "one tracked entity must fire the coalescer exactly once, not once per redundant EnsureHooked call");
  }
}
