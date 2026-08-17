#pragma warning disable CA1707

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lineage;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Fold-before-discard on a real database: a stream's collapsed apply path folds into the
/// persisted signature counts (same RLE as the live query), survives the destruction of its
/// events, and the unfiltered flow view unions persisted shapes with live ones. Filtered queries
/// stay live-only — folded shapes carry no perspective or scope identity to filter by.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/112_StreamGroupCascadeAndApplyPathFold.sql</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresApplyStackQuery.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class ApplyPathFoldSqlTests : EFCoreTestBase {

  private async Task _seedStreamAsync(Guid streamId, params string[] eventTypes) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    for (var version = 1; version <= eventTypes.Length; version++) {
      await using var cmd = new NpgsqlCommand(
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, version, created_at) " +
        "VALUES (@e, @s, @s, 'TestAggregate', @t, @v, NOW())", conn);
      cmd.Parameters.AddWithValue("e", (Guid)TrackedGuid.NewMedo());
      cmd.Parameters.AddWithValue("s", streamId);
      cmd.Parameters.AddWithValue("t", eventTypes[version - 1]);
      cmd.Parameters.AddWithValue("v", version);
      await cmd.ExecuteNonQueryAsync();
    }
  }

  private IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  [Test]
  [Timeout(60000)]
  public async Task Fold_TheStreamDies_ItsShapeSurvives_AndTheUnfilteredViewUnionsItAsync(CancellationToken cancellationToken) {
    var folded = Guid.NewGuid();
    var live = Guid.NewGuid();
    await _seedStreamAsync(folded, "Created", "Updated", "Updated", "Updated", "Closed");
    await _seedStreamAsync(live, "Created", "Updated", "Updated", "Closed");
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    // Fold, then destroy the folded stream's events — fold-before-discard.
    var shapes = await coordinator.FoldStreamApplyPathsAsync([folded], cancellationToken);
    await Assert.That(shapes).IsEqualTo(1);
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync(cancellationToken);
      await using var wipe = new NpgsqlCommand("DELETE FROM wh_event_store WHERE stream_id = @s", conn);
      wipe.Parameters.AddWithValue("s", folded);
      await wipe.ExecuteNonQueryAsync(cancellationToken);
    }

    // The unfiltered flow view: both streams collapse to the SAME shape (3× and 2× Updated both
    // RLE to Updated+), so the union must merge the persisted count with the live one.
    var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped(
      services, _ => CreateDbContext());
    await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
    var query = new EFCorePostgresApplyStackQuery(
      Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(provider),
      typeof(WorkCoordinationDbContext));

    var signatures = await query.GetPathSignaturesAsync(new ApplyStackQueryOptions(), cancellationToken);
    var shape = signatures.Single(s => s.Path.SequenceEqual(["Created", "Updated+", "Closed"]));
    await Assert.That(shape.StreamCount).IsEqualTo(2L)
      .Because("one stream is live, one survives only as its folded shape — the union counts both; "
             + "the destroyed stream's lineage is not lost");

    // Filtered queries stay live-only: folded shapes carry no perspective identity.
    var filtered = await query.GetPathSignaturesAsync(
      new ApplyStackQueryOptions { PerspectiveName = "SomePerspective" }, cancellationToken);
    await Assert.That(filtered.Where(s => s.Path.SequenceEqual(["Created", "Updated+", "Closed"]))).Count().IsEqualTo(0)
      .Because("no association rows exist for this perspective, and persisted shapes must not leak into filtered views");
  }

  [Test]
  [Timeout(60000)]
  public async Task Fold_SameShapeTwice_IncrementsTheCountAsync(CancellationToken cancellationToken) {
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    await _seedStreamAsync(first, "Opened", "Settled");
    await _seedStreamAsync(second, "Opened", "Settled");
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    await coordinator.FoldStreamApplyPathsAsync([first], cancellationToken);
    await coordinator.FoldStreamApplyPathsAsync([second], cancellationToken);

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var count = new NpgsqlCommand(
      "SELECT stream_count FROM wh_apply_paths WHERE path = ARRAY['Opened','Settled']::text[]", conn);
    var streamCount = Convert.ToInt64(
      await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(streamCount).IsEqualTo(2L)
      .Because("aggregate size scales with distinct shapes — same shape folds into one row's count");
  }

  [Test]
  [Timeout(60000)]
  public async Task Fold_IsWatermarked_RefoldingTheSameStreamCountsNothingAsync(CancellationToken cancellationToken) {
    var stream = Guid.NewGuid();
    await _seedStreamAsync(stream, "WmOpened", "WmClosed");
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var first = await coordinator.FoldStreamApplyPathsAsync([stream], cancellationToken);
    var second = await coordinator.FoldStreamApplyPathsAsync([stream], cancellationToken);

    await Assert.That(first).IsEqualTo(1);
    await Assert.That(second).IsEqualTo(0)
      .Because("the watermark makes fold-once a MECHANISM — a re-close, a prune after a close, and "
             + "the settled sweep can all call the fold without coordinating, and the count still "
             + "moves exactly once");

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await using var count = new NpgsqlCommand(
      "SELECT stream_count FROM wh_apply_paths WHERE path = ARRAY['WmOpened','WmClosed']::text[]", conn);
    var streamCount = Convert.ToInt64(
      await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(streamCount).IsEqualTo(1L)
      .Because("double-counting a shape would misreport the fleet's real flow distribution");
  }

  [Test]
  [Timeout(60000)]
  public async Task SettledFold_FoldsIdleStreamsOnly_AndOnlyOnceAsync(CancellationToken cancellationToken) {
    var settled = Guid.NewGuid();
    var live = Guid.NewGuid();
    await _seedStreamAsync(settled, "SettledSpecialA", "SettledSpecialB");
    await _seedStreamAsync(live, "LiveSpecialA", "LiveSpecialB");
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync(cancellationToken);
      await using var age = new NpgsqlCommand(
        "UPDATE wh_event_store SET created_at = NOW() - INTERVAL '2 hours' WHERE stream_id = @s", conn);
      age.Parameters.AddWithValue("s", settled);
      await age.ExecuteNonQueryAsync(cancellationToken);
    }
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var folded = await coordinator.FoldSettledApplyPathsAsync(TimeSpan.FromHours(1), 1000, cancellationToken);
    await Assert.That(folded).IsGreaterThanOrEqualTo(1)
      .Because("a stream idle past the window folds without anything having closed or pruned it — "
             + "the shape census must not depend on destruction ever happening");

    await using var check = new NpgsqlConnection(ConnectionString);
    await check.OpenAsync(cancellationToken);
    await using (var wm = new NpgsqlCommand(
      "SELECT COUNT(*) FROM wh_apply_fold_watermarks WHERE stream_id = @s", check)) {
      wm.Parameters.AddWithValue("s", settled);
      var marked = Convert.ToInt64(await wm.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
      await Assert.That(marked).IsEqualTo(1L);
    }
    await using (var liveWm = new NpgsqlCommand(
      "SELECT COUNT(*) FROM wh_apply_fold_watermarks WHERE stream_id = @s", check)) {
      liveWm.Parameters.AddWithValue("s", live);
      var marked = Convert.ToInt64(await liveWm.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
      await Assert.That(marked).IsEqualTo(0L)
        .Because("a stream still receiving events is not settled — folding it early would freeze a "
               + "shape that is still growing");
    }

    // Once folded, the settled sweep never re-counts it.
    _ = await coordinator.FoldSettledApplyPathsAsync(TimeSpan.FromHours(1), 1000, cancellationToken);
    await using var count = new NpgsqlCommand(
      "SELECT stream_count FROM wh_apply_paths WHERE path = ARRAY['SettledSpecialA','SettledSpecialB']::text[]", check);
    var streamCount = Convert.ToInt64(
      await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(streamCount).IsEqualTo(1L);
  }

  [Test]
  [Timeout(60000)]
  public async Task SettledFoldClaim_FirstInstanceWins_SiblingsSkipAsync(CancellationToken cancellationToken) {
    await using (var conn = new NpgsqlConnection(ConnectionString)) {
      await conn.OpenAsync(cancellationToken);
      await using var reset = new NpgsqlCommand(
        "DELETE FROM wh_settings WHERE setting_key = 'settled_fold_last_run'", conn);
      await reset.ExecuteNonQueryAsync(cancellationToken);
    }
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var winner = await coordinator.TryClaimSettledFoldSweepAsync(TimeSpan.FromHours(24), cancellationToken);
    var loser = await coordinator.TryClaimSettledFoldSweepAsync(TimeSpan.FromHours(24), cancellationToken);

    await Assert.That(winner).IsTrue();
    await Assert.That(loser).IsFalse()
      .Because("the settled fold scans the whole store's idle tail — one instance per window does "
             + "it; every pod doing it would multiply the heaviest read in the maintenance family");
  }
}
