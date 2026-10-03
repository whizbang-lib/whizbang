using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Model = Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation.PhysicalJsonbContainmentSqlTests.JsonbColumnsModel;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// How a promoted jsonb column's value is bound and read: through the column's own converter, under the
/// persistence profile, so the atomic upsert, the change-tracker write and every read agree.
/// </summary>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-columns</docs>
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
public class PhysicalJsonbColumnBindingTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=jsonbbind;Username=u;Password=p";
  private static readonly string[] _oneElement = ["a"];

  private static PhysicalJsonbContainmentSqlTests.JsonbColumnsDbContext _context() {
    Model.Register();
    return new PhysicalJsonbContainmentSqlTests.JsonbColumnsDbContext(
      new DbContextOptionsBuilder<PhysicalJsonbContainmentSqlTests.JsonbColumnsDbContext>()
        .UseNpgsql(UNUSED_CONNECTION)
        .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
        .Options);
  }

  [Test]
  public async Task AJsonbColumn_IsBoundAsJsonbTextThroughItsConverterAsync() {
    await using var context = _context();
    var property = context.Model.FindEntityType(typeof(PerspectiveRow<Model>))!.FindProperty("tags");

    var parameter = BaseUpsertStrategy.PhysicalColumnParameter("pf_0", property, new List<string> { "a", "b" });

    await Assert.That(parameter.NpgsqlDbType).IsEqualTo(NpgsqlDbType.Jsonb);
    await Assert.That(parameter.Value).IsEqualTo("[\"a\",\"b\"]")
      .Because("a list of strings left to inference is a text[] the jsonb column refuses.");
  }

  [Test]
  public async Task EveryOtherColumn_IsBoundAsItsValueAsync() {
    await using var context = _context();
    var entity = context.Model.FindEntityType(typeof(PerspectiveRow<Model>))!;

    var withoutConverter = BaseUpsertStrategy.PhysicalColumnParameter("pf_0", entity.FindProperty("Version"), 3);
    var unknownColumn = BaseUpsertStrategy.PhysicalColumnParameter("pf_1", null, "x");
    var nullValue = BaseUpsertStrategy.PhysicalColumnParameter("pf_2", entity.FindProperty("tags"), null);

    await Assert.That(withoutConverter.Value).IsEqualTo(3);
    await Assert.That(unknownColumn.Value).IsEqualTo("x");
    await Assert.That(nullValue.Value).IsEqualTo(DBNull.Value);
  }

  [Test]
  public async Task AConvertedColumnThatIsNotJsonb_IsBoundAsItsValueAsync() {
    await using var context = new ConvertedTextDbContext(
      new DbContextOptionsBuilder<ConvertedTextDbContext>().UseNpgsql(UNUSED_CONNECTION)
        .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)).Options);
    var property = context.Model.FindEntityType(typeof(PerspectiveRow<Model>))!.FindProperty("code");

    var parameter = BaseUpsertStrategy.PhysicalColumnParameter("pf_0", property, 7);

    await Assert.That(parameter.Value).IsEqualTo(7);
  }

  /// <summary>
  /// The change-tracker write restores the document's copy of each jsonb column it set, and of nothing else: a
  /// column the write did not set, a column whose document holds no copy (Split), and a write with no values
  /// get no statement.
  /// </summary>
  [Test]
  public async Task DocumentCopySql_CoversOnlyTheJsonbColumnsTheWriteSetAsync() {
    await using var context = _context();
    PerspectivePhysicalFieldRegistry.Register(typeof(Model), nameof(Model.Tags), "tags", FieldStorageMode.Extracted, columnType: "jsonb");
    PerspectivePhysicalFieldRegistry.Register(typeof(Model), nameof(Model.Counts), "counts", FieldStorageMode.Extracted, columnType: "jsonb");
    PerspectivePhysicalFieldRegistry.Register(typeof(Model), nameof(Model.Labels), "labels", FieldStorageMode.Split, columnType: "jsonb");

    var both = BaseUpsertStrategy.DocumentCopySql<Model>(context, new Dictionary<string, object?> {
      ["tags"] = new List<string>(),
      ["counts"] = null,
      ["labels"] = null,
      ["title"] = "x",
    });
    var one = BaseUpsertStrategy.DocumentCopySql<Model>(context, new Dictionary<string, object?> { ["tags"] = null });

    await Assert.That(both).IsEqualTo(
      "UPDATE \"wh_per_jsonb_columns\" SET data = CASE WHEN \"tags\" IS NULL THEN (CASE WHEN \"counts\" IS NULL THEN (data) - 'Counts' "
      + "ELSE jsonb_set(data, ARRAY['Counts'], \"counts\") END) - 'Tags' ELSE jsonb_set(CASE WHEN \"counts\" IS NULL THEN (data) - 'Counts' "
      + "ELSE jsonb_set(data, ARRAY['Counts'], \"counts\") END, ARRAY['Tags'], \"tags\") END WHERE id = @wb_id");
    await Assert.That(one).Contains("ARRAY['Tags']");
    await Assert.That(one).DoesNotContain("Counts");
    await Assert.That(BaseUpsertStrategy.DocumentCopySql<Model>(context, new Dictionary<string, object?> { ["labels"] = null })).IsNull();
    await Assert.That(BaseUpsertStrategy.DocumentCopySql<Model>(context, new Dictionary<string, object?>())).IsNull();
    await Assert.That(BaseUpsertStrategy.DocumentCopySql<Model>(context, null)).IsNull();
  }

  [Test]
  public async Task StoredName_IsTheJsonName_OrTheMembersOwnAsync() {
    Model.Register();

    await Assert.That(PerspectiveDocumentSerialization.StoredName(typeof(PhysicalJsonbContainmentSqlTests.JsonbLabel), "Key")).IsEqualTo("k");
    await Assert.That(PerspectiveDocumentSerialization.StoredName(typeof(PhysicalJsonbContainmentSqlTests.JsonbLabel), "Missing")).IsEqualTo("Missing");
    await Assert.That(PerspectiveDocumentSerialization.StoredName(typeof(PhysicalJsonbContainmentSqlTests.JsonbUnlisted), "Name")).IsEqualTo("Name");
  }

  [Test]
  public async Task AStoredJsonNull_ReadsAsTheDefaultAsync() {
    await Assert.That(PerspectiveDocumentSerialization.DeserializeColumn<List<string>>("null")).IsNull();
    await Assert.That(PerspectiveDocumentSerialization.DeserializeColumn<List<string>>("[\"a\"]")).IsEquivalentTo(_oneElement);
  }

  [Test]
  public async Task TheDocumentMarkers_AreNeverCalledAsync() {
    await Assert.That(() => JsonbDocument.Value("a")).Throws<NotSupportedException>();
    await Assert.That(() => JsonbDocument.Value(Guid.Empty)).Throws<NotSupportedException>();
    await Assert.That(() => JsonbDocument.Value(true)).Throws<NotSupportedException>();
    await Assert.That(() => JsonbDocument.Value((byte)1)).Throws<NotSupportedException>();
    await Assert.That(() => JsonbDocument.Value((short)1)).Throws<NotSupportedException>();
    await Assert.That(() => JsonbDocument.Value(1)).Throws<NotSupportedException>();
    await Assert.That(() => JsonbDocument.Value(1L)).Throws<NotSupportedException>();
    await Assert.That(() => JsonbDocument.Value(1m)).Throws<NotSupportedException>();
    await Assert.That(() => JsonbDocument.Member("k", "v")).Throws<NotSupportedException>();
    await Assert.That(() => JsonbDocument.Array("v")).Throws<NotSupportedException>();
    await Assert.That(() => JsonbDocument.Merge("a", "b")).Throws<NotSupportedException>();
  }

  private sealed class ConvertedTextDbContext(DbContextOptions<ConvertedTextDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
      modelBuilder.Entity<PerspectiveRow<Model>>(e => {
        e.HasKey(r => r.Id);
        e.Ignore(r => r.Data);
        e.Ignore(r => r.Metadata);
        e.Ignore(r => r.Scope);
        e.Property<int>("code").HasColumnType("text").HasConversion<string>();
      });
  }
}
