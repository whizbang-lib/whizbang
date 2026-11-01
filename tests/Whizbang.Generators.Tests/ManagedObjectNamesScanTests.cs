// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using ManagedObjectNames = shared::Whizbang.Generators.Shared.Utilities.ManagedObjectNames;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Reading the managed objects out of a table's DDL is a scan. It used to be two regular expressions under a
/// one-second match timeout, which measures the wall clock rather than the work: a generator thread a loaded build
/// machine descheduled for a second threw <see cref="RegexMatchTimeoutException"/>, and the schema generator emitted
/// no source at all (#1942). These tests pin the scan to what the expressions returned, over real generated DDL and
/// over the edge cases the generator never writes.
/// </summary>
/// <tests>src/Whizbang.Generators.Shared/Utilities/ManagedObjectNames.cs</tests>
public class ManagedObjectNamesScanTests {
  private const string CREATE_INDEX =
    @"\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+(?:CONCURRENTLY\s+)?(?:IF\s+NOT\s+EXISTS\s+)?(""[^""]+""|[A-Za-z_][A-Za-z0-9_$]*)";

  private const string ADD_CONSTRAINT = @"\bADD\s+CONSTRAINT\s+(""[^""]+""|[A-Za-z_][A-Za-z0-9_$]*)";

  // The implementation the scan replaced, kept here as the specification it must agree with. The generous timeout
  // is the test's own and never decides the answer.
  private static string _regexExtract(string sql) {
    var found = new List<string>();
    foreach (var (pattern, kind) in new[] { (CREATE_INDEX, "index"), (ADD_CONSTRAINT, "constraint") }) {
      foreach (Match match in Regex.Matches(sql, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
          TimeSpan.FromMinutes(1))) {
        var name = match.Groups[1].Value;
        var bare = name.Length > 1 && name[0] == '"' ? name[1..^1] : name;
        var entry = $"{kind}:{bare}";
        if (!found.Contains(entry)) {
          found.Add(entry);
        }
      }
    }
    return string.Join("|", found);
  }

  private static string _scanExtract(string sql) =>
    string.Join("|", ManagedObjectNames.Extract(sql).Select(o => $"{o.Kind}:{o.Name}"));

  [Test]
  [Arguments("CREATE INDEX a ON t (x);")]
  [Arguments("create unique index if not exists \"Quoted Name\" on t (x);")]
  [Arguments("CREATE INDEX CONCURRENTLY IF NOT EXISTS b ON t (x);")]
  [Arguments("CREATE UNIQUE INDEX CONCURRENTLY c ON t (x);")]
  [Arguments("CREATE INDEX IF NOT foo ON t;")]
  [Arguments("CREATE INDEX IF x ON t")]
  [Arguments("CREATE INDEX CONCURRENTLY ( ON t")]
  [Arguments("CREATE INDEX IF NOT EXISTS \"open")]
  [Arguments("CREATE INDEX \"\" ON t")]
  [Arguments("CREATE INDEX \"unterminated")]
  [Arguments("CREATE INDEX \"a\"\"b\" ON t")]
  [Arguments("CREATE TABLE t (x int);")]
  [Arguments("CREATE UNIQUE x")]
  [Arguments("CREATEINDEX a")]
  [Arguments("CREATE INDEX")]
  [Arguments("CREATE INDEX   ")]
  [Arguments("CREATE")]
  [Arguments("CREATE INDEX 9abc ON t")]
  [Arguments("CREATE INDEX a$b9_C ON t")]
  [Arguments("CREATE INDEX a[0] ON t")]
  [Arguments("CREATE INDEX `q` ON t")]
  [Arguments("CREATE INDEX z{ ON t")]
  [Arguments("CREATE\tUNIQUE\nINDEX\r\nidx$1_a ON t")]
  [Arguments("CREATE\u00A0INDEX\u2003spaced ON t")]
  [Arguments("CREATE INDEX a ON t; CREATE INDEX b ON t;")]
  [Arguments("CREATE INDEX CREATE INDEX b ON t")]
  [Arguments("CREATE INDEX a")]
  [Arguments("xCREATE INDEX a ON t")]
  [Arguments("_CREATE INDEX a ON t")]
  [Arguments("1CREATE INDEX a ON t")]
  [Arguments("\u0301CREATE INDEX a ON t")]
  [Arguments("\u200CCREATE INDEX a ON t")]
  [Arguments("\u200DCREATE INDEX a ON t")]
  [Arguments("\u0903CREATE INDEX a ON t")]
  [Arguments("\u2160CREATE INDEX a ON t")]
  [Arguments("$CREATE INDEX a ON t")]
  [Arguments("(CREATE INDEX a ON t")]
  [Arguments("ALTER TABLE t ADD CONSTRAINT ck CHECK (x > 0)")]
  [Arguments("ALTER TABLE t ADD  CONSTRAINT\n\"Quoted Check\" CHECK (x > 0)")]
  [Arguments("ALTER TABLE t ADD COLUMN x int")]
  [Arguments("ALTER TABLE t ADD CONSTRAINT (x)")]
  [Arguments("ADDCONSTRAINT a")]
  [Arguments("aADD CONSTRAINT b")]
  [Arguments("ADD CONSTRAINT")]
  [Arguments("ADD")]
  [Arguments("ADD CONSTRAINT c1 CHECK (x); ADD CONSTRAINT c1 CHECK (x); CREATE INDEX c1 ON t")]
  [Arguments("")]
  public async Task EdgeCases_ReadExactlyAsTheExpressionsDidAsync(string sql) {
    await Assert.That(_scanExtract(sql)).IsEqualTo(_regexExtract(sql));
  }

  /// <summary>
  /// The DDL the generator really writes for a model with every kind of managed object: the standard GIN indexes,
  /// a physical column's index, a unique one and a length constraint.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task GeneratedPerspectiveDdl_ReadsExactlyAsTheExpressionsDidAsync() {
    const string source = """
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace MyApp.Perspectives;

      [PerspectiveStorage(FieldStorageMode.Split)]
      public record LedgerModel {
        public Guid Id { get; set; }

        [PhysicalField(MaxLength = 64)]
        [Indexed]
        public string Code { get; set; } = string.Empty;

        [PhysicalField(Unique = true)]
        public string Reference { get; set; } = string.Empty;
      }

      public record LedgerChanged : IEvent;

      public class LedgerPerspective : IPerspectiveFor<LedgerModel, LedgerChanged> {
        public LedgerModel Apply(LedgerModel currentData, LedgerChanged @event) => currentData;
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<PerspectiveSchemaGenerator>(source);
    var generated = GeneratorTestHelper.GetRequiredGeneratedSource(result, "PerspectiveSchemas.g.sql.cs");

    var expected = _regexExtract(generated);
    await Assert.That(expected).Contains("constraint:")
      .Because("the comparison has to cover a constraint as well as indexes, or it proves half the scan");
    await Assert.That(_scanExtract(generated)).IsEqualTo(expected);
  }
}
