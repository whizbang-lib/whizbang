using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the effective-expiry ladder at the lens: an explicit <c>expires_at</c> overrides the rule,
/// otherwise the sliding window derives from <c>updated_at</c>, otherwise the row never expires.
/// </summary>
/// <remarks>
/// <para>
/// The behavioural change is that a NULL <c>expires_at</c> now means "fall through to the rule"
/// rather than "never expires". That is what lets a perspective adopt retention and immediately
/// govern rows written before the declaration existed, with no data migration.
/// </para>
/// <para>
/// The guard on TTL presence is load-bearing, not stylistic: <c>ResolveSeconds</c> returns
/// <c>-1</c> — not null — for an unregistered model, a per-model override set to null, and the
/// global kill switch being off. Deriving a window from <c>-1</c> would place expiry one second
/// BEFORE the row's own business time, so every row of every ungoverned perspective would read as
/// expired, and flipping the switch that exists to STOP expiry would instead expire the fleet.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
[NotInParallel("EffectiveExpiryLadder")]
public class EffectiveExpiryLadderTests : EFCoreTestBase {
  private const string TABLE = "wh_per_expiry_ladder";

  private async Task _seedAsync(NpgsqlConnection conn, Guid id, DateTime updatedAt, DateTime? expiresAt) {
    await using var cmd = new NpgsqlCommand($@"
      INSERT INTO {TABLE} (id, data, metadata, scope, created_at, updated_at, version, expires_at)
      VALUES (@id, '{{}}'::jsonb, '{{}}'::jsonb, '{{}}'::jsonb, @u, @u, 1, @e)", conn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("u", updatedAt);
    cmd.Parameters.Add(new NpgsqlParameter("e", NpgsqlTypes.NpgsqlDbType.TimestampTz) {
      Value = (object?)expiresAt ?? DBNull.Value
    });
    await cmd.ExecuteNonQueryAsync();
  }

  private async Task _createTableAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand($@"
      DROP TABLE IF EXISTS {TABLE};
      CREATE TABLE {TABLE} (
        id UUID NOT NULL PRIMARY KEY,
        data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL,
        sys_created_at TIMESTAMPTZ, sys_updated_at TIMESTAMPTZ,
        expires_at TIMESTAMPTZ, version INTEGER NOT NULL);", conn);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task NullExpiresAt_FallsThroughToTheSlidingRuleAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _createTableAsync(conn);

    var stale = Guid.CreateVersion7();
    var fresh = Guid.CreateVersion7();
    // Both predate any declaration, so neither carries an expiry — exactly the historical rows that
    // adopting retention has to govern without a migration.
    await _seedAsync(conn, stale, DateTime.UtcNow.AddDays(-90), null);
    await _seedAsync(conn, fresh, DateTime.UtcNow.AddDays(-1), null);

    PerspectiveTtlRegistry.Register(typeof(LadderModel), 60 * 60 * 24 * 60);
    try {
      await using var context = _createContext();
      var visible = await LensExpiryFilter.Apply(context.Set<PerspectiveRow<LadderModel>>())
        .Select(r => r.Id).ToListAsync();

      await Assert.That(visible).Contains(fresh)
        .Because("a row inside the sliding window stays visible");
      await Assert.That(visible).DoesNotContain(stale)
        .Because("NULL expires_at must mean 'fall through to the rule', not 'never expires' — that is "
          + "what governs rows written before the perspective declared retention");
    } finally {
      PerspectiveTtlRegistry.Register(typeof(LadderModel), -1);
    }
  }

  [Test]
  public async Task ExplicitExpiry_OverridesTheSlidingRuleAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _createTableAsync(conn);

    // Idle far past the sliding window, but explicitly pinned into the future.
    var pinned = Guid.CreateVersion7();
    await _seedAsync(conn, pinned, DateTime.UtcNow.AddDays(-90), DateTime.UtcNow.AddDays(30));

    PerspectiveTtlRegistry.Register(typeof(LadderModel), 60 * 60 * 24 * 60);
    try {
      await using var context = _createContext();
      var visible = await LensExpiryFilter.Apply(context.Set<PerspectiveRow<LadderModel>>())
        .Select(r => r.Id).ToListAsync();

      await Assert.That(visible).Contains(pinned)
        .Because("an explicit expires_at replaces the sliding term — pinning a row is the capability "
          + "the column exists for once the rule is derived");
    } finally {
      PerspectiveTtlRegistry.Register(typeof(LadderModel), -1);
    }
  }

  [Test]
  public async Task NoTtlDeclared_ExpiresNothingAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _createTableAsync(conn);

    var ancient = Guid.CreateVersion7();
    await _seedAsync(conn, ancient, DateTime.UtcNow.AddYears(-5), null);

    // ResolveSeconds returns -1 here, exactly as it does when the kill switch is off.
    await using var context = _createContext();
    var visible = await LensExpiryFilter.Apply(context.Set<PerspectiveRow<LadderModel>>())
      .Select(r => r.Id).ToListAsync();

    await Assert.That(visible).Contains(ancient)
      .Because("-1 means ungoverned, and deriving a window from it would place expiry one second "
        + "before the row's own business time — expiring the fleet the moment an operator disables expiry");
  }

  private LadderDbContext _createContext() =>
    new(new DbContextOptionsBuilder<LadderDbContext>().UseNpgsql(ConnectionString).Options);

  internal sealed class LadderDbContext(DbContextOptions<LadderDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      base.OnModelCreating(modelBuilder);
      modelBuilder.Entity<PerspectiveRow<LadderModel>>(entity => {
        entity.ToTable(TABLE);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, sc => sc.ToJson("scope"));
        entity.Property<DateTime?>("sys_created_at").HasColumnName("sys_created_at");
        entity.Property<DateTime?>("sys_updated_at").HasColumnName("sys_updated_at");
        entity.Property<DateTime?>("expires_at").HasColumnName("expires_at");
      });
    }
  }
}

/// <summary>Minimal model for the expiry-ladder tests.</summary>
public record LadderModel {
  public string Name { get; init; } = "";
}
