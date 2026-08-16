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
}
