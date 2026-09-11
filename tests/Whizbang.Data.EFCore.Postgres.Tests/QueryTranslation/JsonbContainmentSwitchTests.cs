using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// The kill switch for the containment rewrite: on by default, and able to put the previous SQL back
/// without a release.
/// </summary>
/// <remarks>
/// The two SQL-shape cases deliberately filter on different properties. Entity Framework caches a
/// compiled query by its original expression, so reusing one predicate across a flip would read the
/// cached plan and prove nothing.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#index-advisories</docs>
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class JsonbContainmentSwitchTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=gate;Username=u;Password=p";

  [SuppressIndexAdvisory("compiled, never run")]
  public class GateModel {
    public string Title { get; init; } = string.Empty;
    public Guid Owner { get; init; }
  }

  private sealed class GateDbContext(DbContextOptions<GateDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<GateModel>>(entity => {
        entity.ToTable("wh_per_gate");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => {
          s.ToJson("scope");
          s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });
        entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        entity.Property(e => e.Version).HasColumnName("version").IsRequired();
      });

      modelBuilder.UseWhizbangJsonbContainment();
    }
  }

  private static readonly DbContextOptions<GateDbContext> _options =
    new DbContextOptionsBuilder<GateDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  [After(Test)]
  public void RestoreDefault() => JsonbContainmentSwitch.Reset();

  /// <summary>On is the default, so an upgrade gets the index without being asked.</summary>
  [Test]
  public async Task Default_IsOnAsync() {
    JsonbContainmentSwitch.Reset();
    await Assert.That(JsonbContainmentSwitch.Enabled).IsTrue();
  }

  /// <summary>Operator configuration turns it off.</summary>
  [Test]
  public async Task ApplyRuntimeConfiguration_TurnsItOffAsync() {
    JsonbContainmentSwitch.ApplyRuntimeConfiguration(new PerspectiveQueryTranslationOptions {
      UseJsonbContainment = false,
    });

    await Assert.That(JsonbContainmentSwitch.Enabled).IsFalse();
  }

  /// <summary>And back on again, so a fallback is not a one-way door.</summary>
  [Test]
  public async Task ApplyRuntimeConfiguration_TurnsItBackOnAsync() {
    JsonbContainmentSwitch.Set(false);
    JsonbContainmentSwitch.ApplyRuntimeConfiguration(new PerspectiveQueryTranslationOptions {
      UseJsonbContainment = true,
    });

    await Assert.That(JsonbContainmentSwitch.Enabled).IsTrue();
  }

  /// <summary>The default options value is on, which is what binding an absent section produces.</summary>
  [Test]
  public async Task Options_DefaultToOnAsync() {
    await Assert.That(new PerspectiveQueryTranslationOptions().UseJsonbContainment).IsTrue();
  }

  /// <summary>Null options are rejected rather than silently treated as off.</summary>
  [Test]
  public async Task ApplyRuntimeConfiguration_RejectsNullAsync() {
    await Assert.That(() => JsonbContainmentSwitch.ApplyRuntimeConfiguration(null!))
      .Throws<ArgumentNullException>();
  }

  /// <summary>With the switch on, an equality filter compiles to containment.</summary>
  [Test]
  public async Task On_CompilesToContainmentAsync() {
    JsonbContainmentSwitch.Set(true);

    using var db = new GateDbContext(_options);
    var sql = db.Set<PerspectiveRow<GateModel>>().Where(x => x.Data.Title == "v").ToQueryString();

    await Assert.That(sql).Contains("@>", StringComparison.Ordinal);
    await Assert.That(sql).Contains("jsonb_build_object", StringComparison.Ordinal);
  }

  /// <summary>With it off, the same kind of filter compiles to the extraction the framework used before.</summary>
  [Test]
  public async Task Off_CompilesToExtractionAsync() {
    JsonbContainmentSwitch.Set(false);

    using var db = new GateDbContext(_options);
    var owner = Guid.NewGuid();
    var sql = db.Set<PerspectiveRow<GateModel>>().Where(x => x.Data.Owner == owner).ToQueryString();

    await Assert.That(sql).DoesNotContain("@>", StringComparison.Ordinal);
    await Assert.That(sql).Contains("data ->>", StringComparison.Ordinal);
  }
}
