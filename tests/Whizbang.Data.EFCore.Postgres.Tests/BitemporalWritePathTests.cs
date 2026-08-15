using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the two time axes on the perspective-row write path: <c>created_at</c> / <c>updated_at</c>
/// carry BUSINESS time (the applied event's timestamp, replay-invariant) while
/// <c>sys_created_at</c> / <c>sys_updated_at</c> carry SYSTEM time (wall clock at write).
/// </summary>
/// <remarks>
/// <para>
/// The distinguishing test is replay: re-applying the same events must reproduce identical
/// business time while system time advances. Before this split both columns took the clock, so a
/// rebuild rewrote every row — every entity's created date became the rebuild moment and recency
/// ordering collapsed to write order.
/// </para>
/// <para>
/// These assert exact equality against explicitly-supplied event timestamps rather than measuring
/// elapsed wall time, so there is nothing to sleep on and nothing to flake.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
[NotInParallel("BitemporalWritePath")]
public class BitemporalWritePathTests : EFCoreTestBase {
  private const string TABLE = "wh_per_bitemporal_write";

  private async Task _createTableAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand($@"
      DROP TABLE IF EXISTS {TABLE};
      CREATE TABLE {TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL,
        metadata JSONB NOT NULL,
        scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL,
        updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ,
        sys_updated_at TIMESTAMPTZ,
        expires_at TIMESTAMPTZ,
        version INTEGER NOT NULL);", conn);
    await cmd.ExecuteNonQueryAsync();
  }

  private static PerspectiveMetadata _eventAt(DateTime occurredAt) => new() {
    EventType = "TestOccurred",
    EventId = Guid.CreateVersion7().ToString(),
    Timestamp = occurredAt,
  };

  private static async Task<(DateTime Created, DateTime Updated, DateTime? SysCreated, DateTime? SysUpdated)>
      _readAxesAsync(NpgsqlConnection conn, Guid id) {
    await using var cmd = new NpgsqlCommand(
      $"SELECT created_at, updated_at, sys_created_at, sys_updated_at FROM {TABLE} WHERE id = @id", conn);
    cmd.Parameters.AddWithValue("id", id);
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    return (
      reader.GetDateTime(0),
      reader.GetDateTime(1),
      reader.IsDBNull(2) ? null : reader.GetDateTime(2),
      reader.IsDBNull(3) ? null : reader.GetDateTime(3));
  }

  [Test]
  public async Task Insert_AnchorsBusinessTimeToTheEvent_AndSystemTimeToTheClockAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _createTableAsync(conn);

    var id = Guid.CreateVersion7();
    var occurredAt = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    var beforeWrite = DateTime.UtcNow;

    await using var context = _createContext();
    IPerspectiveStore<BitemporalWriteModel> store =
      new EFCorePostgresPerspectiveStore<BitemporalWriteModel>(context, TABLE, new PostgresUpsertStrategy());
    await store.UpsertAsync(id, new BitemporalWriteModel { Name = "first" },
      new PerspectiveScope(), false, _eventAt(occurredAt));

    var axes = await _readAxesAsync(conn, id);

    await Assert.That(axes.Created).IsEqualTo(occurredAt)
      .Because("created_at is business time — when the entity came into being, taken from the event, "
        + "not the moment the row happened to be written");
    await Assert.That(axes.Updated).IsEqualTo(occurredAt)
      .Because("updated_at is business time — the applied event's own timestamp");
    await Assert.That(axes.SysCreated).IsNotNull();
    await Assert.That(axes.SysCreated!.Value).IsGreaterThanOrEqualTo(beforeWrite)
      .Because("sys_created_at is system time — the wall clock at write, which is 'now' regardless of "
        + "how long ago the event occurred");
  }

  [Test]
  public async Task Update_AdvancesBusinessTimeToTheNewEvent_AndPreservesCreatedAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _createTableAsync(conn);

    var id = Guid.CreateVersion7();
    var firstEvent = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    var secondEvent = new DateTime(2021, 11, 12, 13, 14, 15, DateTimeKind.Utc);

    await using var context = _createContext();
    IPerspectiveStore<BitemporalWriteModel> store =
      new EFCorePostgresPerspectiveStore<BitemporalWriteModel>(context, TABLE, new PostgresUpsertStrategy());

    await store.UpsertAsync(id, new BitemporalWriteModel { Name = "first" },
      new PerspectiveScope(), false, _eventAt(firstEvent));
    await store.UpsertAsync(id, new BitemporalWriteModel { Name = "second" },
      new PerspectiveScope(), false, _eventAt(secondEvent));

    var axes = await _readAxesAsync(conn, id);

    await Assert.That(axes.Created).IsEqualTo(firstEvent)
      .Because("the entity still came into being when the FIRST event occurred");
    await Assert.That(axes.Updated).IsEqualTo(secondEvent)
      .Because("last business activity moves to the newly applied event's timestamp — exact, not "
        + "'some time after the previous write', so there is nothing to sleep on");
  }

  [Test]
  public async Task Replay_ReproducesBusinessTime_WhileSystemTimeAdvancesAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _createTableAsync(conn);

    var id = Guid.CreateVersion7();
    var firstEvent = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    var secondEvent = new DateTime(2021, 11, 12, 13, 14, 15, DateTimeKind.Utc);

    await using var context = _createContext();
    IPerspectiveStore<BitemporalWriteModel> store =
      new EFCorePostgresPerspectiveStore<BitemporalWriteModel>(context, TABLE, new PostgresUpsertStrategy());

    await store.UpsertAsync(id, new BitemporalWriteModel { Name = "first" },
      new PerspectiveScope(), false, _eventAt(firstEvent));
    await store.UpsertAsync(id, new BitemporalWriteModel { Name = "second" },
      new PerspectiveScope(), false, _eventAt(secondEvent));
    var beforeReplay = await _readAxesAsync(conn, id);

    // A rebuild re-applies the SAME events through the SAME path, now.
    await store.UpsertAsync(id, new BitemporalWriteModel { Name = "first" },
      new PerspectiveScope(), false, _eventAt(firstEvent));
    await store.UpsertAsync(id, new BitemporalWriteModel { Name = "second" },
      new PerspectiveScope(), false, _eventAt(secondEvent));
    var afterReplay = await _readAxesAsync(conn, id);

    await Assert.That(afterReplay.Updated).IsEqualTo(beforeReplay.Updated)
      .Because("business time is a pure function of the event log, so a rebuild reproduces it exactly — "
        + "this is what makes retention, recency ordering and 'what changed' survive a rebuild");
    await Assert.That(afterReplay.SysUpdated!.Value).IsGreaterThanOrEqualTo(beforeReplay.SysUpdated!.Value)
      .Because("system time legitimately advances — the row really was written again");
  }

  private BitemporalWriteDbContext _createContext() {
    var options = new DbContextOptionsBuilder<BitemporalWriteDbContext>()
      .UseNpgsql(ConnectionString)
      .Options;
    return new BitemporalWriteDbContext(options);
  }

  private sealed class BitemporalWriteDbContext(DbContextOptions<BitemporalWriteDbContext> options)
      : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<BitemporalWriteModel>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, sc => sc.ToJson("scope"));
        // System-time axis as SHADOW properties, mirroring the generated production config — a CLR
        // property would be auto-mapped into every hand-configured context and break reads on tables
        // that predate the columns (the hazard expires_at already documents).
        entity.Property<DateTime?>("sys_created_at").HasColumnName("sys_created_at");
        entity.Property<DateTime?>("sys_updated_at").HasColumnName("sys_updated_at");
        entity.Property<DateTime?>("expires_at").HasColumnName("expires_at");
      });
    }
  }
}

/// <summary>Minimal model for the write-path axis tests.</summary>
public record BitemporalWriteModel {
  public string Name { get; init; } = "";
}
