using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// The compiled SQL of a filter on a promoted jsonb column: every supported shape becomes a containment
/// test with the bare column on the left, which is the one form its GIN index answers, and every shape
/// that containment cannot express is left exactly as it was.
/// </summary>
/// <remarks>
/// Nothing opens a connection; <c>ToQueryString</c> is the whole observation. The end-to-end proof that
/// the planner uses the index and that the rows match is <see cref="PhysicalJsonbContainmentIntegrationTests"/>.
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields#jsonb-columns</docs>
[NotInParallel("EFCorePostgresTests")]
[Category("Shard1")]
[SuppressMessage("Readability", "RCS1118:Mark local variable as const",
  Justification = "Captured on purpose: a const local is inlined as a literal and the parameterized path would go untested.")]
public class PhysicalJsonbContainmentSqlTests {
  private const string UNUSED_CONNECTION = "Host=localhost;Database=jsonbcols;Username=u;Password=p";

  private static readonly DbContextOptions<JsonbColumnsDbContext> _options =
    new DbContextOptionsBuilder<JsonbColumnsDbContext>()
      .UseNpgsql(UNUSED_CONNECTION)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options;

  private static JsonbColumnsDbContext _context() {
    JsonbColumnsModel.Register();
    JsonbContainmentSwitch.Reset();
    return new JsonbColumnsDbContext(_options);
  }

  private static string _sql(Func<IQueryable<PerspectiveRow<JsonbColumnsModel>>, IQueryable<PerspectiveRow<JsonbColumnsModel>>> shape) {
    using var context = _context();
    return shape(context.Set<PerspectiveRow<JsonbColumnsModel>>()).ToQueryString();
  }

  // ------------------------------------------------------------------
  // Dictionary of collections: a key's values contain a value
  // ------------------------------------------------------------------

  [Test]
  public async Task DictionaryKeyContains_Param_CompilesToContainmentOfAKeyedArrayAsync() {
    var key = "region";
    var value = "north";

    var sql = _sql(rows => rows.Where(r => r.Data.GridFilter[key].Contains(value)));

    // Entity Framework evaluates a dictionary indexer's captured key into the query as a literal, so the key
    // is part of the compiled query's shape and only the value is a parameter.
    await Assert.That(sql).Contains("w.grid_filter @> jsonb_build_object('region', jsonb_build_array(to_jsonb(@value)))");
  }

  [Test]
  public async Task DictionaryKeyContains_Constant_CompilesToContainmentAsync() {
    var sql = _sql(rows => rows.Where(r => r.Data.GridFilter["region"].Contains("north")));

    await Assert.That(sql).Contains("w.grid_filter @> jsonb_build_object('region', jsonb_build_array(to_jsonb('north'::text)))");
  }

  [Test]
  public async Task DictionaryKeyAny_EqualToValue_CompilesLikeContainsAsync() {
    var value = "north";

    var sql = _sql(rows => rows.Where(r => r.Data.GridFilter["region"].Any(v => v == value)));

    await Assert.That(sql).Contains("w.grid_filter @> jsonb_build_object('region', jsonb_build_array(to_jsonb(@value)))");
  }

  [Test]
  public async Task DictionaryKeyEquals_ScalarValue_CompilesToContainmentOfTheKeyAsync() {
    var count = 3;

    var sql = _sql(rows => rows.Where(r => r.Data.Counts["open"] == count));

    await Assert.That(sql).Contains("w.counts @> jsonb_build_object('open', to_jsonb(@count))");
  }

  // ------------------------------------------------------------------
  // Collections
  // ------------------------------------------------------------------

  [Test]
  public async Task ScalarListContains_CompilesToContainmentOfAnArrayAsync() {
    var tag = "red";

    var sql = _sql(rows => rows.Where(r => r.Data.Tags.Contains(tag)));

    await Assert.That(sql).Contains("w.tags @> jsonb_build_array(to_jsonb(@tag))");
  }

  [Test]
  public async Task ScalarArrayContains_ThroughEnumerable_CompilesToContainmentAsync() {
    var id = Guid.NewGuid();

    var sql = _sql(rows => rows.Where(r => r.Data.Owners.Contains(id)));

    await Assert.That(sql).Contains("w.owners @> jsonb_build_array(to_jsonb(@id))");
  }

  [Test]
  public async Task ObjectListAny_ConjunctionOfMembers_CompilesToOneElementAsync() {
    var key = "k";
    var label = "l";

    var sql = _sql(rows => rows.Where(r => r.Data.Labels.Any(l => l.Key == key && l.Label == label)));

    await Assert.That(sql).Contains(
      "w.labels @> jsonb_build_array(jsonb_build_object('k', to_jsonb(@key)) || jsonb_build_object('Label', to_jsonb(@label)))");
  }

  // ------------------------------------------------------------------
  // Objects
  // ------------------------------------------------------------------

  [Test]
  public async Task ObjectMemberEquals_Nested_CompilesToNestedContainmentAsync() {
    var city = "Springfield";

    var sql = _sql(rows => rows.Where(r => r.Data.Location!.Address.City == city));

    await Assert.That(sql).Contains(
      "w.location @> jsonb_build_object('Address', jsonb_build_object('City', to_jsonb(@city)))");
  }

  [Test]
  public async Task ObjectMemberEquals_ValueFirst_CompilesTheSameAsync() {
    var zone = 4;

    var sql = _sql(rows => rows.Where(r => zone == r.Data.Location!.Zone));

    await Assert.That(sql).Contains("w.location @> jsonb_build_object('Zone', to_jsonb(@zone))");
  }

  [Test]
  public async Task Negated_IsTheNegatedContainmentTestAsync() {
    var tag = "red";

    var sql = _sql(rows => rows.Where(r => !r.Data.Tags.Contains(tag)));

    await Assert.That(sql).Contains("NOT (w.tags @> jsonb_build_array(to_jsonb(@tag)))");
  }

  [Test]
  public async Task TwoFilters_AreTwoContainmentTestsAsync() {
    var tag = "red";
    var value = "north";

    var sql = _sql(rows => rows.Where(r => r.Data.Tags.Contains(tag) && r.Data.GridFilter["region"].Contains(value)));

    await Assert.That(sql).Contains("w.tags @> jsonb_build_array(to_jsonb(@tag))");
    await Assert.That(sql).Contains("w.grid_filter @> jsonb_build_object('region', jsonb_build_array(to_jsonb(@value)))");
  }

  [Test]
  public async Task OffSwitch_DoesNotStopIt_BecauseNothingElseTranslatesTheShapeAsync() {
    var tag = "red";
    using var context = _context();
    JsonbContainmentSwitch.SetMode(ContainmentMode.Off);
    try {
      var sql = context.Set<PerspectiveRow<JsonbColumnsModel>>().Where(r => r.Data.Tags.Contains(tag)).ToQueryString();

      await Assert.That(sql).Contains("w.tags @> jsonb_build_array(to_jsonb(@tag))");
    } finally {
      JsonbContainmentSwitch.Reset();
    }
  }

  // ------------------------------------------------------------------
  // The test model
  // ------------------------------------------------------------------

  /// <summary>An element of a list of objects; <c>Key</c> is renamed in its stored form.</summary>
  public sealed record JsonbLabel([property: JsonPropertyName("k")] string Key, string Label);

  /// <summary>A nested object.</summary>
  public sealed class JsonbAddress {
    public string City { get; set; } = "";
  }

  /// <summary>A type no serializer context describes.</summary>
  public sealed class JsonbUnlisted {
    public string Name { get; set; } = "";
  }

  /// <summary>An object column.</summary>
  public sealed class JsonbLocation {
    public JsonbAddress Address { get; set; } = new();
    public int Zone { get; set; }

    /// <summary>Never stored, so a filter on it has no stored name to compile against.</summary>
    [JsonIgnore]
    public string? Hidden { get; set; }

    /// <summary>Left out only when null, so a value is stored under its name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
  }

  /// <summary>A read model whose filter values live in promoted jsonb columns.</summary>
  [SuppressIndexAdvisory("a hand-built model under test: the test registers its columns, the generator does not")]
  public sealed class JsonbColumnsModel {
    public string Title { get; set; } = "";
    public Dictionary<string, string[]> GridFilter { get; set; } = [];
    public Dictionary<string, int> Counts { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public Guid[] Owners { get; set; } = [];
    public List<JsonbLabel> Labels { get; set; } = [];
    public JsonbLocation? Location { get; set; }
    public List<DateTime> Stamps { get; set; } = [];
    public List<JsonbLocation> Places { get; set; } = [];
    public List<Dictionary<string, string>> Rows { get; set; } = [];
    public Dictionary<int, string> ByNumber { get; set; } = [];

    /// <summary>A column whose type the serializer has no metadata for.</summary>
    [JsonIgnore]
    public JsonbUnlisted? Other { get; set; }

    private static int _registered;

    internal static void Register() {
      if (Interlocked.Exchange(ref _registered, 1) == 1) {
        return;
      }

      JsonContextRegistry.RegisterContext(JsonbColumnsJsonContext.Default);
      PhysicalFieldRegistry.Register<JsonbColumnsModel>(nameof(GridFilter), "grid_filter");
      PhysicalFieldRegistry.Register<JsonbColumnsModel>(nameof(Counts), "counts");
      PhysicalFieldRegistry.Register<JsonbColumnsModel>(nameof(Tags), "tags");
      PhysicalFieldRegistry.Register<JsonbColumnsModel>(nameof(Owners), "owners");
      PhysicalFieldRegistry.Register<JsonbColumnsModel>(nameof(Labels), "labels");
      PhysicalFieldRegistry.Register<JsonbColumnsModel>(nameof(Location), "location");
      PhysicalFieldRegistry.Register<JsonbColumnsModel>(nameof(Stamps), "stamps");
      PhysicalFieldRegistry.Register<JsonbColumnsModel>(nameof(Places), "places");
      PhysicalFieldRegistry.Register<JsonbColumnsModel>(nameof(Rows), "rows");
      PhysicalFieldRegistry.Register<JsonbColumnsModel>(nameof(ByNumber), "by_number");
      PhysicalFieldRegistry.Register<JsonbColumnsModel>(nameof(Other), "other");
    }
  }

  /// <summary>The model as the generator configures it: the promoted jsonb fields are columns, not document members.</summary>
  public sealed class JsonbColumnsDbContext(DbContextOptions<JsonbColumnsDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<PerspectiveRow<JsonbColumnsModel>>(entity => {
        entity.ToTable("wh_per_jsonb_columns");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => {
          d.ToJson("data");
          d.Ignore(m => m.GridFilter);
          d.Ignore(m => m.Counts);
          d.Ignore(m => m.Tags);
          d.Ignore(m => m.Owners);
          d.Ignore(m => m.Labels);
          d.Ignore(m => m.Location);
          d.Ignore(m => m.Stamps);
          d.Ignore(m => m.Places);
          d.Ignore(m => m.Rows);
          d.Ignore(m => m.ByNumber);
          d.Ignore(m => m.Other);
        });
        entity.ComplexProperty(e => e.Metadata, m => m.ToJson("metadata"));
        entity.ComplexProperty(e => e.Scope, s => {
          s.ToJson("scope");
          s.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });
        entity.Property(e => e.CreatedAt).HasColumnName("created_at");
        entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        entity.Property(e => e.Version).HasColumnName("version");

        entity.Property<Dictionary<string, string[]>>("grid_filter").HasColumnName("grid_filter").HasColumnType("jsonb")
          .HasConversion(PerspectiveDocumentSerialization.ColumnConverterFor<Dictionary<string, string[]>>());
        entity.Property<Dictionary<string, int>>("counts").HasColumnName("counts").HasColumnType("jsonb")
          .HasConversion(PerspectiveDocumentSerialization.ColumnConverterFor<Dictionary<string, int>>());
        entity.Property<List<string>>("tags").HasColumnName("tags").HasColumnType("jsonb")
          .HasConversion(PerspectiveDocumentSerialization.ColumnConverterFor<List<string>>());
        entity.Property<Guid[]>("owners").HasColumnName("owners").HasColumnType("jsonb")
          .HasConversion(PerspectiveDocumentSerialization.ColumnConverterFor<Guid[]>());
        entity.Property<List<JsonbLabel>>("labels").HasColumnName("labels").HasColumnType("jsonb")
          .HasConversion(PerspectiveDocumentSerialization.ColumnConverterFor<List<JsonbLabel>>());
        entity.Property<JsonbLocation?>("location").HasColumnName("location").HasColumnType("jsonb")
          .HasConversion(PerspectiveDocumentSerialization.ColumnConverterFor<JsonbLocation?>());
        _jsonb<List<DateTime>>(entity, "stamps");
        _jsonb<List<JsonbLocation>>(entity, "places");
        _jsonb<List<Dictionary<string, string>>>(entity, "rows");
        _jsonb<Dictionary<int, string>>(entity, "by_number");
        _jsonb<JsonbUnlisted?>(entity, "other");
      });

      modelBuilder.UseWhizbangJsonbContainment();
    }

    private static void _jsonb<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PerspectiveRow<JsonbColumnsModel>> entity, string column) =>
      entity.Property<T>(column).HasColumnName(column).HasColumnType("jsonb")
        .HasConversion(PerspectiveDocumentSerialization.ColumnConverterFor<T>());
  }
}

/// <summary>Serialization metadata for the test model, as a generated context would provide it.</summary>
[JsonSerializable(typeof(PhysicalJsonbContainmentSqlTests.JsonbColumnsModel))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Guid[]))]
[JsonSerializable(typeof(List<PhysicalJsonbContainmentSqlTests.JsonbLabel>))]
[JsonSerializable(typeof(PhysicalJsonbContainmentSqlTests.JsonbLocation))]
[JsonSerializable(typeof(List<DateTime>))]
[JsonSerializable(typeof(List<PhysicalJsonbContainmentSqlTests.JsonbLocation>))]
[JsonSerializable(typeof(List<Dictionary<string, string>>))]
[JsonSerializable(typeof(Dictionary<int, string>))]
internal sealed partial class JsonbColumnsJsonContext : JsonSerializerContext;
