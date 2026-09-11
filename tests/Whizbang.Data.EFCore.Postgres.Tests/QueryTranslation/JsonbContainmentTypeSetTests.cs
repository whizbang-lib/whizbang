using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Pins the set of types a containment rewrite is allowed to touch.
/// </summary>
/// <remarks>
/// <para>
/// The same set is duplicated in <c>PerspectiveFilterIndexAnalyzer</c>, which decides whether WHIZ302
/// should stay quiet because containment already indexes the filter. The duplication is forced: an
/// analyzer is referenced as an analyzer rather than as a library, so neither assembly can see the
/// other's list and no single test can compare them.
/// </para>
/// <para>
/// So each side pins its own, and each names the other. Adding a type here without adding it to
/// <c>EqualityContainmentCanServe_IsNotReportedAsync</c> in the generators tests leaves the analyzer
/// warning about a filter that is already a lookup; removing one without removing it there leaves a
/// filter that scans with no advisory. Neither produces a wrong answer, which is why the pin is a
/// list rather than a build break.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/jsonb-containment</docs>
[Category("Shard1")]
public class JsonbContainmentTypeSetTests {
  /// <summary>
  /// Exactly these types, and the reason each is here: the text the serializer writes and the text
  /// PostgreSQL generates for the same value are the same, so a containment test means what the
  /// equality it replaces meant.
  /// </summary>
  [Test]
  public async Task EligibleTypes_AreExactlyTheOnesWhoseTextFormsAgreeAsync() {
    // Compared by name: a deep equivalence over Type recurses into members that throw for
    // non-generic types, and the names are what a reader of this test wants to see anyway.
    var expected = new[] {
      typeof(string), typeof(Guid), typeof(bool),
      typeof(short), typeof(int), typeof(long), typeof(decimal),
      typeof(double), typeof(float), typeof(byte),
    }.Select(t => t.FullName!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    var actual = JsonbContainment.Overloads
      .Select(m => m.GetParameters()[0].ParameterType.FullName!)
      .OrderBy(n => n, StringComparer.Ordinal)
      .ToArray();

    await Assert.That(actual).IsEquivalentTo(expected);
  }

  /// <summary>
  /// The excluded types, named so that adding one is a deliberate act with a reason rather than an
  /// oversight. The date and time family is excluded for two different reasons, both measured: a
  /// DateTime is written with a trailing Z where PostgreSQL generates an explicit offset, and a
  /// DateTimeOffset preserves the offset it was written with while equality compares instants, so
  /// two values equal in .NET can be stored as different text.
  /// </summary>
  [Test]
  [Arguments(typeof(DateTime))]
  [Arguments(typeof(DateTimeOffset))]
  [Arguments(typeof(DateOnly))]
  [Arguments(typeof(TimeOnly))]
  [Arguments(typeof(char))]
  [Arguments(typeof(TimeSpan))]
  public async Task ExcludedTypes_HaveNoOverloadAsync(Type excluded) {
    await Assert.That(JsonbContainment.OverloadFor(excluded)).IsNull();
  }

  /// <summary>Diagnostic: what the model walk sees for an enumeration inside a JSON complex property.</summary>
  [Test]
  public async Task EnumPropertyMetadata_IsRecordedAsync() {
    using var db = new MatrixProbeContext(
      new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<MatrixProbeContext>()
        .UseNpgsql("Host=localhost;Database=meta;Username=u;Password=p")
        .Options);

    var row = db.Model.FindEntityType(typeof(Whizbang.Core.Lenses.PerspectiveRow<MetaModel>));
    var complex = row?.FindComplexProperty("Data")?.ComplexType;
    var leaf = complex?.FindProperty(nameof(MetaModel.State));

    var report = $"row={row is not null} complex={complex is not null} leaf={leaf is not null} " +
      $"clr={leaf?.ClrType.Name} valueConverter={leaf?.GetValueConverter()?.GetType().Name ?? "none"} " +
      $"mappingConverter={leaf?.GetTypeMapping().Converter?.GetType().Name ?? "none"} " +
      $"provider={leaf?.GetTypeMapping().Converter?.ProviderClrType.Name ?? "none"}";

    var target = Environment.GetEnvironmentVariable("WHIZ_META_DUMP");
    if (!string.IsNullOrWhiteSpace(target)) {
      await File.WriteAllTextAsync(target, report);
    }

    await Assert.That(report).IsNotEmpty();
  }

  public enum Shade { Dim, Bright }

  [Whizbang.Core.Perspectives.SuppressIndexAdvisory("metadata probe")]
  public class MetaModel {
    public Shade State { get; init; }
  }

  private sealed class MatrixProbeContext(Microsoft.EntityFrameworkCore.DbContextOptions<MatrixProbeContext> o)
    : Microsoft.EntityFrameworkCore.DbContext(o) {
    protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder b) =>
      b.Entity<Whizbang.Core.Lenses.PerspectiveRow<MetaModel>>(e => {
        e.ToTable("wh_per_meta");
        e.HasKey(x => x.Id);
        e.ComplexProperty(x => x.Data, d => d.ToJson("data"));
        e.ComplexProperty(x => x.Metadata, m => m.ToJson("metadata"));
        e.ComplexProperty(x => x.Scope, sc => {
          sc.ToJson("scope");
          sc.ComplexCollection(p => p.Extensions, ex => ex.HasJsonPropertyName("ex"));
        });
      });
  }

  /// <summary>An enumeration resolves to the overload for its underlying numeric type.</summary>
  [Test]
  public async Task Enumerations_ResolveToTheirUnderlyingOverloadAsync() {
    var overload = JsonbContainment.OverloadFor(typeof(DayOfWeek));

    await Assert.That(overload).IsNotNull();
    await Assert.That(overload!.GetParameters()[0].ParameterType).IsEqualTo(typeof(int));
  }

  /// <summary>A nullable member resolves to its underlying overload, since only non-null values are rewritten.</summary>
  [Test]
  [Arguments(typeof(int?), typeof(int))]
  [Arguments(typeof(Guid?), typeof(Guid))]
  [Arguments(typeof(bool?), typeof(bool))]
  [Arguments(typeof(decimal?), typeof(decimal))]
  public async Task NullableTypes_ResolveToTheUnderlyingOverloadAsync(Type nullable, Type underlying) {
    var overload = JsonbContainment.OverloadFor(nullable);

    await Assert.That(overload).IsNotNull();
    await Assert.That(overload!.GetParameters()[0].ParameterType).IsEqualTo(underlying);
  }
}
