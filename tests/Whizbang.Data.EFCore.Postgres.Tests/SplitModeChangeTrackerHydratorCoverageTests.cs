using Microsoft.EntityFrameworkCore.ChangeTracking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for <see cref="SplitModeChangeTrackerHydrator.Clear"/> — the test-only reset of the
/// static hydrator registry. No database needed: the registry is an in-process
/// <c>ConcurrentDictionary</c>, and <c>Clear</c> is a pure bookkeeping operation.
/// <para>
/// Production impact if this regresses: <c>Clear</c> exists specifically so test suites can
/// reset Split-mode hydrator registration between runs without cross-test bleed (the dictionary
/// is <c>private static</c> — process-global). If it silently stopped clearing, a hydrator
/// registered by one test's generated code would keep firing for a later test's differently-shaped
/// model, corrupting that test's assertions in a way that looks like the code under test is wrong.
/// </para>
/// <para>
/// <c>_hydrators</c> is process-global and NOT scoped by any per-class constraint key of its own
/// — the same hazard already recorded for <c>IntegrityManifestReceptors._pagesFollowed</c> in
/// <c>scratchpad/residue.md</c>. Tagged with the shared <c>"EFCorePostgresTests"</c>
/// <see cref="NotInParallelAttribute"/> key (the same one every <c>EFCoreTestBase</c> test and
/// <c>SplitModeProductionTests</c> already carry) so <c>Clear()</c> can never run concurrently
/// with another test's own <c>Register</c> call — without it, wiping the registry mid-run would
/// corrupt whichever other Split-mode test happened to be mid-hydration at that moment.
/// </para>
/// </summary>
[Category("Shard1")]
[NotInParallel("EFCorePostgresTests")]
public class SplitModeChangeTrackerHydratorCoverageTests {

  private sealed class _coverageProbeModel;

  [Test]
  public async Task Clear_AfterRegistering_RemovesTheHydratorAsync() {
    var probeType = typeof(_coverageProbeModel);
    SplitModeChangeTrackerHydrator.Register(probeType, static _ => { });

    await Assert.That(SplitModeChangeTrackerHydrator.HasHydrator(probeType)).IsTrue()
      .Because("registration must be visible before we can prove Clear removes it");

    SplitModeChangeTrackerHydrator.Clear();

    await Assert.That(SplitModeChangeTrackerHydrator.HasHydrator(probeType)).IsFalse()
      .Because("Clear must actually empty the registry, not just report success");
  }
}
