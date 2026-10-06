// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using EFCorePerspectiveConfigurationGenerator = Whizbang.Data.EFCore.Postgres.Generators.EFCorePerspectiveConfigurationGenerator;
using EFCoreServiceRegistrationGenerator = Whizbang.Data.EFCore.Postgres.Generators.EFCoreServiceRegistrationGenerator;
using PhysicalColumnSql = shared::Whizbang.Generators.Shared.Models.PhysicalColumnSql;
using PhysicalFieldInfo = shared::Whizbang.Generators.Shared.Models.PhysicalFieldInfo;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Pins the four type-name tables the generators use to type a physical field's column: the
/// schema-extension DDL, the perspective table DDL, the EF Core model, and the backfill that reads a
/// field's value out of the document. Each table is matched exactly and ordinally, so every mapped
/// name gets its column type and every name that is one character away from a mapped one gets the
/// default.
/// </summary>
/// <remarks>
/// The near misses are not padding. The compiler lowers a string switch to a dispatch on length and
/// then on one character, followed by a full comparison, so a name that agrees with a mapped one on
/// length and on the dispatch character reaches that comparison and must be refused there. Replacing
/// each character of each mapped name in turn produces exactly those names, and the ones that differ
/// on the dispatch character, without the test having to know which character the compiler chose.
/// A table that matched on a prefix, ignored case, or trimmed would map some of them and fail here.
/// </remarks>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCoreServiceRegistrationGenerator.cs</tests>
/// <tests>src/Whizbang.Generators/PerspectiveSchemaGenerator.cs</tests>
/// <tests>src/Whizbang.Data.EFCore.Postgres.Generators/EFCorePerspectiveConfigurationGenerator.cs</tests>
/// <tests>src/Whizbang.Generators.Shared/Models/PhysicalColumnSql.cs</tests>
[Category("SourceGenerators")]
public class GeneratorColumnTypeTableTests {
  // Characters no mapped name contains, so a substitution can never produce another mapped name: one
  // below and one above every character the names use, because the compiler's dispatch on a character
  // splits that range and refuses an unmatched character separately on each side.
  private static readonly string[] _foreign = ["!", "\u0416"];

  private static readonly (string Name, string Type)[] _registrationTable = [
    ("System.Guid", "UUID"),
    ("System.String", "TEXT"), ("string", "TEXT"),
    ("System.Int32", "INTEGER"), ("int", "INTEGER"),
    ("System.Int64", "BIGINT"), ("long", "BIGINT"),
    ("System.Int16", "SMALLINT"), ("short", "SMALLINT"),
    ("System.Boolean", "BOOLEAN"), ("bool", "BOOLEAN"),
    ("System.DateTime", "TIMESTAMPTZ"),
    ("System.DateTimeOffset", "TIMESTAMPTZ"),
    ("System.DateOnly", "DATE"),
    ("System.TimeOnly", "TIME"),
    ("System.Decimal", "NUMERIC"), ("decimal", "NUMERIC"),
    ("System.Double", "DOUBLE PRECISION"), ("double", "DOUBLE PRECISION"),
    ("System.Single", "REAL"), ("float", "REAL"),
    ("System.Byte[]", "BYTEA"), ("byte[]", "BYTEA"),
    ("float[]", "REAL[]"),
    ("double[]", "DOUBLE PRECISION[]"),
  ];

  private static readonly (string Name, string Type)[] _schemaTable = [
    ("System.String", "TEXT"), ("string", "TEXT"),
    ("System.Int32", "INTEGER"), ("int", "INTEGER"),
    ("System.Int64", "BIGINT"), ("long", "BIGINT"),
    ("System.Int16", "SMALLINT"), ("short", "SMALLINT"),
    ("System.Decimal", "DECIMAL"), ("decimal", "DECIMAL"),
    ("System.Double", "DOUBLE PRECISION"), ("double", "DOUBLE PRECISION"),
    ("System.Single", "REAL"), ("float", "REAL"),
    ("System.Boolean", "BOOLEAN"), ("bool", "BOOLEAN"),
    ("System.Guid", "UUID"),
    ("System.DateTime", "TIMESTAMP"),
    ("System.DateTimeOffset", "TIMESTAMPTZ"),
    ("System.DateOnly", "DATE"),
    ("System.TimeOnly", "TIME"),
    ("System.Single[]", "REAL[]"), ("float[]", "REAL[]"),
  ];

  private static readonly (string Name, string Type)[] _efCoreTable = [
    ("System.String", "text"), ("string", "text"),
    ("System.Int32", "integer"), ("int", "integer"),
    ("System.Int64", "bigint"), ("long", "bigint"),
    ("System.Int16", "smallint"), ("short", "smallint"),
    ("System.Decimal", "decimal"), ("decimal", "decimal"),
    ("System.Double", "double precision"), ("double", "double precision"),
    ("System.Single", "real"), ("float", "real"),
    ("System.Boolean", "boolean"), ("bool", "boolean"),
    ("System.Guid", "uuid"),
    ("System.DateTime", "timestamptz"),
    ("System.DateTimeOffset", "timestamptz"),
    ("System.DateOnly", "date"),
    ("System.TimeOnly", "time"),
  ];

  private const string MICROS = "(data ->> 'Value')::bigint * INTERVAL '1 microsecond'";

  private static readonly (string Name, string Read)[] _backfillTable = [
    ("System.String", "(data ->> 'Value')"), ("string", "(data ->> 'Value')"),
    ("System.Guid", "(data ->> 'Value')::uuid"),
    ("System.Int32", "(data ->> 'Value')::integer"), ("int", "(data ->> 'Value')::integer"),
    ("System.Int64", "(data ->> 'Value')::bigint"), ("long", "(data ->> 'Value')::bigint"),
    ("System.Int16", "(data ->> 'Value')::smallint"), ("short", "(data ->> 'Value')::smallint"),
    ("System.Boolean", "(data ->> 'Value')::boolean"), ("bool", "(data ->> 'Value')::boolean"),
    ("System.Decimal", "(data ->> 'Value')::numeric"), ("decimal", "(data ->> 'Value')::numeric"),
    ("System.Double", "(data ->> 'Value')::double precision"), ("double", "(data ->> 'Value')::double precision"),
    ("System.Single", "(data ->> 'Value')::real"), ("float", "(data ->> 'Value')::real"),
    ("System.DateTime", $"(TIMESTAMPTZ 'epoch' + {MICROS})"),
    ("System.DateTimeOffset", $"(TIMESTAMPTZ 'epoch' + {MICROS})"),
    ("System.DateOnly", $"(TIMESTAMP 'epoch' + {MICROS})::date"),
    ("System.TimeOnly", $"(TIME '00:00' + {MICROS})"),
  ];

  /// <summary>Every one-character substitution of every mapped name, plus a name of every length.</summary>
  private static List<string> _nearMisses(IEnumerable<string> names) {
    // Every length up to past the longest mapped name: the dispatch on length has a slot per length in
    // that range, mapped or not, and a name of an unmapped length is refused there.
    var misses = Enumerable.Range(0, 41).Select(n => new string('y', n)).ToList();
    foreach (var name in names) {
      for (var i = 0; i < name.Length; i++) {
        foreach (var foreign in _foreign) {
          misses.Add(string.Concat(name.AsSpan(0, i), foreign, name.AsSpan(i + 1)));
        }
      }
    }
    return misses;
  }

  private static List<string> _mismatches(IEnumerable<(string Name, string? Expected)> table, Func<string, string?> map) =>
    [.. table.Select(row => (row.Name, row.Expected, Actual: map(row.Name)))
      .Where(row => row.Actual != row.Expected)
      .Select(row => $"{row.Name}: expected {row.Expected ?? "null"}, got {row.Actual ?? "null"}")];

  private static string? _backfill(string typeName) =>
    PhysicalColumnSql.Extraction(new PhysicalFieldInfo(
      PropertyName: "Value", ColumnName: "value", TypeName: typeName, IsIndexed: false, IsUnique: false,
      MaxLength: null, IsVector: false, VectorDimensions: null, VectorDistanceMetric: null,
      VectorIndexType: null, VectorIndexLists: null));

  [Test]
  public async Task RegistrationColumnType_MapsEveryKnownNameAsync() {
    var wrong = _mismatches(_registrationTable.Select(r => (r.Name, (string?)r.Type)), EFCoreServiceRegistrationGenerator.PostgresColumnTypeFor);
    await Assert.That(wrong).IsEmpty();
  }

  [Test]
  public async Task RegistrationColumnType_NearMissNames_FallBackToDefaultAsync() {
    var misses = _nearMisses(_registrationTable.Select(r => r.Name));
    var wrong = _mismatches(misses.Select(m => (m, (string?)"TEXT")), EFCoreServiceRegistrationGenerator.PostgresColumnTypeFor);
    await Assert.That(wrong).IsEmpty()
      .Because("only an exact, ordinal match is a mapped type; anything else is the TEXT default");
  }

  [Test]
  public async Task SchemaColumnType_MapsEveryKnownNameAsync() {
    var wrong = _mismatches(_schemaTable.Select(r => (r.Name, (string?)r.Type)), PerspectiveSchemaGenerator.PostgresTypeFor);
    await Assert.That(wrong).IsEmpty();
  }

  [Test]
  public async Task SchemaColumnType_NearMissNames_FallBackToDefaultAsync() {
    var misses = _nearMisses(_schemaTable.Select(r => r.Name));
    var wrong = _mismatches(misses.Select(m => (m, (string?)"TEXT")), PerspectiveSchemaGenerator.PostgresTypeFor);
    await Assert.That(wrong).IsEmpty()
      .Because("only an exact, ordinal match is a mapped type; anything else is the TEXT default");
  }

  [Test]
  public async Task EFCoreColumnType_MapsEveryKnownNameAsync() {
    var wrong = _mismatches(_efCoreTable.Select(r => (r.Name, (string?)r.Type)), EFCorePerspectiveConfigurationGenerator.EFCoreColumnTypeFor);
    await Assert.That(wrong).IsEmpty();
  }

  [Test]
  public async Task EFCoreColumnType_NearMissNames_FallBackToDefaultAsync() {
    var misses = _nearMisses(_efCoreTable.Select(r => r.Name));
    var wrong = _mismatches(misses.Select(m => (m, (string?)"text")), EFCorePerspectiveConfigurationGenerator.EFCoreColumnTypeFor);
    await Assert.That(wrong).IsEmpty()
      .Because("only an exact, ordinal match is a mapped type; anything else is the text default");
  }

  /// <summary>A missing name matches no mapped type, so each table gives its default.</summary>
  [Test]
  public async Task NoName_IsTheDefaultInEveryTableAsync() {
    await Assert.That(EFCoreServiceRegistrationGenerator.PostgresColumnTypeFor(null!)).IsEqualTo("TEXT");
    await Assert.That(PerspectiveSchemaGenerator.PostgresTypeFor(null!)).IsEqualTo("TEXT");
    await Assert.That(EFCorePerspectiveConfigurationGenerator.EFCoreColumnTypeFor(null!)).IsEqualTo("text");
  }

  [Test]
  public async Task BackfillExtraction_ReadsEveryKnownScalarAsync() {
    var wrong = _mismatches(_backfillTable.Select(r => (r.Name, (string?)r.Read)), _backfill);
    await Assert.That(wrong).IsEmpty();
  }

  [Test]
  public async Task BackfillExtraction_NearMissNames_AreNotReproducibleAsync() {
    var misses = _nearMisses(_backfillTable.Select(r => r.Name));
    var wrong = _mismatches(misses.Select(m => (m, (string?)null)), _backfill);
    await Assert.That(wrong).IsEmpty()
      .Because("a type the backfill does not know cannot be read back exactly, so it gets no extraction");
  }
}
