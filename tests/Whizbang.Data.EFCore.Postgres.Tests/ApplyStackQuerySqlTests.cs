#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lineage;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The apply-stack query end to end against a real database: per-stream version-ordered paths
/// from event-store pointers, run-length collapsed in SQL, grouped into signature counts. Locks
/// the collapse semantics (runs of 2+ merge under one <c>+</c> element, a single occurrence stays
/// plain and is a DIFFERENT signature), the perspective filter through the association registry,
/// the scope containment filter, drill-in consistency with the signature listing, and the
/// heaviest-first limit.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresApplyStackQuery.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class ApplyStackQuerySqlTests : EFCoreTestBase {

  private static readonly DateTimeOffset SEED_BASE = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  private IApplyStackQuery _buildQuery(out ServiceProvider provider) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    provider = services.BuildServiceProvider();
    return new EFCorePostgresApplyStackQuery(
      provider.GetRequiredService<IServiceScopeFactory>(), typeof(WorkCoordinationDbContext));
  }

  private static async Task _seedStreamAsync(
      NpgsqlConnection connection, Guid streamId, string? scopeJson, params string[] eventTypes) {
    for (var version = 1; version <= eventTypes.Length; version++) {
      await using var cmd = connection.CreateCommand();
      cmd.CommandText =
        "INSERT INTO wh_event_store (event_id, stream_id, aggregate_id, aggregate_type, event_type, scope, version, created_at) " +
        "VALUES (@event_id, @stream_id, @stream_id, 'TestAggregate', @event_type, " +
        "        CASE WHEN @scope IS NULL THEN NULL ELSE @scope::jsonb END, @version, @created_at)";
      cmd.Parameters.AddWithValue("event_id", (Guid)TrackedGuid.NewMedo());
      cmd.Parameters.AddWithValue("stream_id", streamId);
      cmd.Parameters.AddWithValue("event_type", eventTypes[version - 1]);
      cmd.Parameters.Add(new NpgsqlParameter("scope", NpgsqlTypes.NpgsqlDbType.Text) {
        Value = (object?)scopeJson ?? DBNull.Value
      });
      cmd.Parameters.AddWithValue("version", version);
      cmd.Parameters.AddWithValue("created_at", SEED_BASE.AddMinutes(version));
      await cmd.ExecuteNonQueryAsync();
    }
  }

  private static async Task _seedPerspectiveAssociationAsync(
      NpgsqlConnection connection, string eventType, string perspectiveName) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_message_associations
        (id, message_type, association_type, target_name, service_name, normalized_message_type, created_at, updated_at)
      VALUES (gen_random_uuid(), @t, 'perspective', @p, 'test-service', @t, NOW(), NOW())
      ON CONFLICT DO NOTHING
      """;
    cmd.Parameters.AddWithValue("t", eventType);
    cmd.Parameters.AddWithValue("p", perspectiveName);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  [Timeout(60000)]
  public async Task GetPathSignatures_CollapsesRunsAndGroupsIdenticalShapesAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    // Two streams whose Updated runs differ only in LENGTH (3 vs 7) — the collapse merges them.
    await _seedStreamAsync(conn, Guid.NewGuid(), null, "Created", "Updated", "Updated", "Updated", "Closed");
    await _seedStreamAsync(conn, Guid.NewGuid(), null, "Created", "Updated", "Updated", "Updated", "Updated", "Updated", "Updated", "Updated", "Closed");
    // A single Updated is NOT a run — a genuinely different shape.
    await _seedStreamAsync(conn, Guid.NewGuid(), null, "Created", "Updated", "Closed");

    var query = _buildQuery(out var provider);
    await using var _ = provider;
    var signatures = await query.GetPathSignaturesAsync(new ApplyStackQueryOptions(), cancellationToken);

    await Assert.That(signatures).Count().IsEqualTo(2)
      .Because("runs of 2+ collapse into one '+' element, so 3× and 7× Updated are the SAME shape and 1× is a different one");

    var collapsed = signatures[0];
    await Assert.That(collapsed.Path).IsEquivalentTo(["Created", "Updated+", "Closed"])
      .Because("signatures order heaviest first, and the collapsed shape carries two streams");
    await Assert.That(collapsed.StreamCount).IsEqualTo(2L);

    var plain = signatures[1];
    await Assert.That(plain.Path).IsEquivalentTo(["Created", "Updated", "Closed"]);
    await Assert.That(plain.StreamCount).IsEqualTo(1L);

    await Assert.That(collapsed.LastSeen).IsGreaterThanOrEqualTo(collapsed.FirstSeen)
      .Because("first/last seen bracket the head-event times of the streams sharing the shape");
    await Assert.That(collapsed.FirstSeen).IsGreaterThan(DateTimeOffset.MinValue);
  }

  [Test]
  [Timeout(60000)]
  public async Task GetStreamsForPath_ReturnsExactlyTheStreamsBehindASignatureAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    var streamA = Guid.NewGuid();
    var streamB = Guid.NewGuid();
    var streamC = Guid.NewGuid();
    await _seedStreamAsync(conn, streamA, null, "Created", "Updated", "Updated", "Closed");
    await _seedStreamAsync(conn, streamB, null, "Created", "Updated", "Updated", "Updated", "Closed");
    await _seedStreamAsync(conn, streamC, null, "Created", "Updated", "Closed");

    var query = _buildQuery(out var provider);
    await using var _ = provider;
    var streams = await query.GetStreamsForPathAsync(
      ["Created", "Updated+", "Closed"], new ApplyStackQueryOptions(), limit: 10, cancellationToken);

    await Assert.That(streams).Count().IsEqualTo(2)
      .Because("drill-in must return exactly the streams the signature counted — same CTE, no drift");
    await Assert.That(streams).Contains(streamA);
    await Assert.That(streams).Contains(streamB);
    await Assert.That(streams).DoesNotContain(streamC);
  }

  [Test]
  [Timeout(60000)]
  public async Task GetPathSignatures_PerspectiveFilter_ProjectsThePerspectivesViewOfTheStreamAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _seedPerspectiveAssociationAsync(conn, "Created", "OrderList");
    await _seedPerspectiveAssociationAsync(conn, "Closed", "OrderList");
    await _seedStreamAsync(conn, Guid.NewGuid(), null, "Created", "InternalNoise", "Closed");

    var query = _buildQuery(out var provider);
    await using var _ = provider;

    var filtered = await query.GetPathSignaturesAsync(
      new ApplyStackQueryOptions { PerspectiveName = "OrderList" }, cancellationToken);
    await Assert.That(filtered).Count().IsEqualTo(1);
    await Assert.That(filtered[0].Path).IsEquivalentTo(["Created", "Closed"])
      .Because("the perspective's path is its OWN filtered view of the stream — types it never applies are not in its stack");

    var unfiltered = await query.GetPathSignaturesAsync(new ApplyStackQueryOptions(), cancellationToken);
    await Assert.That(unfiltered[0].Path).IsEquivalentTo(["Created", "InternalNoise", "Closed"])
      .Because("without a perspective filter the whole-store path keeps every type");
  }

  [Test]
  [Timeout(60000)]
  public async Task GetPathSignatures_ScopeFilter_KeepsOnlyContainedScopesAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _seedStreamAsync(conn, Guid.NewGuid(), """{"tenant":"alpha"}""", "Created", "Closed");
    await _seedStreamAsync(conn, Guid.NewGuid(), """{"tenant":"beta"}""", "Created", "Archived");

    var query = _buildQuery(out var provider);
    await using var _ = provider;
    var signatures = await query.GetPathSignaturesAsync(
      new ApplyStackQueryOptions { ScopeJson = """{"tenant":"alpha"}""" }, cancellationToken);

    await Assert.That(signatures).Count().IsEqualTo(1)
      .Because("a tenant-scoped caller sees only its own shapes — JSONB containment on the event scope");
    await Assert.That(signatures[0].Path).IsEquivalentTo(["Created", "Closed"]);
  }

  [Test]
  [Timeout(60000)]
  public async Task GetPathSignatures_LimitKeepsTheHeaviestShapesAsync(CancellationToken cancellationToken) {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync(cancellationToken);
    await _seedStreamAsync(conn, Guid.NewGuid(), null, "Created", "Closed");
    await _seedStreamAsync(conn, Guid.NewGuid(), null, "Created", "Closed");
    await _seedStreamAsync(conn, Guid.NewGuid(), null, "Created", "Archived");

    var query = _buildQuery(out var provider);
    await using var _ = provider;
    var signatures = await query.GetPathSignaturesAsync(
      new ApplyStackQueryOptions { MaxSignatures = 1 }, cancellationToken);

    await Assert.That(signatures).Count().IsEqualTo(1);
    await Assert.That(signatures[0].Path).IsEquivalentTo(["Created", "Closed"])
      .Because("when the listing truncates, it keeps the heaviest shapes — the long tail is what drops");
    await Assert.That(signatures[0].StreamCount).IsEqualTo(2L);
  }
}
