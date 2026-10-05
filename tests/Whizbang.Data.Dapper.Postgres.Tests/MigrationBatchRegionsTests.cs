// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.IO;
using System.Linq;
using TUnit.Assertions.Extensions;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// That a migration carrying a batched region is split into the pieces the runner has to send
/// separately, in the order the file wrote them.
/// </summary>
/// <remarks>
/// <para>
/// A migration file is executed as one command, so its whole body shares one command timeout. A
/// backfill that rewrites every row of a table therefore cannot finish on a large database however
/// the SQL inside it is written: a loop in a <c>DO</c> block is still one command. The marked region
/// exists so the runner can send that statement repeatedly, each send its own command with its own
/// budget, until it reports no rows left to do.
/// </para>
/// <para>
/// Order is the part worth testing hardest. A backfill is marked next to the function it calls, and
/// that function is created by the plain SQL above it, so a split that hoists regions out of place
/// would call something that does not exist yet. Segments come back interleaved, never grouped.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationBatchRegions.cs</code-under-test>
public class MigrationBatchRegionsTests {

  [Test]
  public async Task SqlWithNoMarkersIsOnePlainSegmentAsync() {
    var sql = "CREATE TABLE x (id INT);\nINSERT INTO x VALUES (1);";

    var segments = MigrationBatchRegions.Segment(sql);

    await Assert.That(segments.Count).IsEqualTo(1);
    await Assert.That(segments[0].Kind).IsEqualTo(MigrationSegmentKind.Plain);
    await Assert.That(segments[0].Sql).Contains("CREATE TABLE x");
  }

  [Test]
  public async Task AMarkedRegionBecomesABatchSegmentAsync() {
    var sql = $"""
      CREATE FUNCTION f() RETURNS void AS $$ BEGIN END $$ LANGUAGE plpgsql;
      {MigrationBatchRegions.BEGIN}
      INSERT INTO b SELECT id FROM a WHERE done IS NULL LIMIT {MigrationBatchRegions.SIZE_TOKEN};
      {MigrationBatchRegions.END}
      """;

    var segments = MigrationBatchRegions.Segment(sql);

    await Assert.That(segments.Count).IsEqualTo(2);
    await Assert.That(segments[0].Kind).IsEqualTo(MigrationSegmentKind.Plain);
    await Assert.That(segments[1].Kind).IsEqualTo(MigrationSegmentKind.Batch);
    await Assert.That(segments[1].Sql).Contains("INSERT INTO b");
    await Assert.That(segments[1].Sql).DoesNotContain(MigrationBatchRegions.BEGIN);
  }

  [Test]
  public async Task SegmentsKeepTheOrderTheFileWroteThemAsync() {
    // The backfill is marked next to the function it calls; hoisting it would call a function the
    // plain SQL above has not created yet.
    var sql = $"""
      CREATE FUNCTION first() RETURNS void AS $$ BEGIN END $$ LANGUAGE plpgsql;
      {MigrationBatchRegions.BEGIN}
      UPDATE t SET c = 1 WHERE id IN (SELECT id FROM t WHERE c IS NULL LIMIT {MigrationBatchRegions.SIZE_TOKEN});
      {MigrationBatchRegions.END}
      CREATE INDEX after_the_backfill ON t (c);
      """;

    var segments = MigrationBatchRegions.Segment(sql);

    await Assert.That(segments.Count).IsEqualTo(3);
    await Assert.That(segments[0].Sql).Contains("CREATE FUNCTION first");
    await Assert.That(segments[1].Kind).IsEqualTo(MigrationSegmentKind.Batch);
    await Assert.That(segments[2].Sql).Contains("after_the_backfill");
  }

  [Test]
  public async Task ABatchDeclaresItsSizeAndTheDefaultAppliesWithoutOneAsync() {
    var withSize = $"{MigrationBatchRegions.BEGIN} size=250\nUPDATE t SET c = 1;\n{MigrationBatchRegions.END}";
    var without = $"{MigrationBatchRegions.BEGIN}\nUPDATE t SET c = 1;\n{MigrationBatchRegions.END}";

    var sized = MigrationBatchRegions.Segment(withSize);
    var defaulted = MigrationBatchRegions.Segment(without);

    await Assert.That(sized[0].BatchSize).IsEqualTo(250);
    await Assert.That(defaulted[0].BatchSize).IsEqualTo(MigrationBatchRegions.DEFAULT_BATCH_SIZE);
  }

  [Test]
  public async Task TheSizeTokenIsReplacedWithTheDeclaredSizeAsync() {
    // The runner binds no parameters, so the size has to be substituted into the text.
    var sql = $"{MigrationBatchRegions.BEGIN} size=500\nDELETE FROM t WHERE id IN (SELECT id FROM t WHERE x LIMIT {MigrationBatchRegions.SIZE_TOKEN});\n{MigrationBatchRegions.END}";

    var segments = MigrationBatchRegions.Segment(sql);

    await Assert.That(segments[0].Sql).Contains("LIMIT 500");
    await Assert.That(segments[0].Sql).DoesNotContain(MigrationBatchRegions.SIZE_TOKEN);
  }

  [Test]
  public async Task SeveralRegionsEachBecomeTheirOwnSegmentAsync() {
    var sql = $"""
      {MigrationBatchRegions.BEGIN} size=10
      UPDATE one SET c = 1;
      {MigrationBatchRegions.END}
      SELECT 1;
      {MigrationBatchRegions.BEGIN} size=20
      UPDATE two SET c = 1;
      {MigrationBatchRegions.END}
      """;

    var segments = MigrationBatchRegions.Segment(sql);

    await Assert.That(segments.Count(s => s.Kind == MigrationSegmentKind.Batch)).IsEqualTo(2);
    await Assert.That(segments.First(s => s.Sql.Contains("one")).BatchSize).IsEqualTo(10);
    await Assert.That(segments.First(s => s.Sql.Contains("two")).BatchSize).IsEqualTo(20);
  }

  [Test]
  public async Task AnUnclosedRegionIsRejectedRatherThanRunUnboundedAsync() {
    // Silently treating it as plain SQL would run the very statement the marker exists to bound.
    var sql = $"{MigrationBatchRegions.BEGIN}\nUPDATE t SET c = 1;";

    await Assert.That(() => MigrationBatchRegions.Segment(sql)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task AnEndWithoutABeginIsRejectedAsync() {
    var sql = $"UPDATE t SET c = 1;\n{MigrationBatchRegions.END}";

    await Assert.That(() => MigrationBatchRegions.Segment(sql)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task ANonPositiveOrUnparsableSizeIsRejectedAsync() {
    var zero = $"{MigrationBatchRegions.BEGIN} size=0\nUPDATE t SET c = 1;\n{MigrationBatchRegions.END}";
    var words = $"{MigrationBatchRegions.BEGIN} size=lots\nUPDATE t SET c = 1;\n{MigrationBatchRegions.END}";

    await Assert.That(() => MigrationBatchRegions.Segment(zero)).Throws<InvalidOperationException>();
    await Assert.That(() => MigrationBatchRegions.Segment(words)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task WhitespaceOnlyPlainSegmentsAreDroppedAsync() {
    // The newlines around a marker would otherwise be sent as an empty command.
    var sql = $"\n\n{MigrationBatchRegions.BEGIN}\nUPDATE t SET c = 1;\n{MigrationBatchRegions.END}\n\n";

    var segments = MigrationBatchRegions.Segment(sql);

    await Assert.That(segments.Count).IsEqualTo(1);
    await Assert.That(segments[0].Kind).IsEqualTo(MigrationSegmentKind.Batch);
  }

  [Test]
  public async Task SegmentingIsStableForSqlThatCarriesNoMarkersAsync() {
    // Every existing migration goes through this path, so the no-marker case must be byte-identical
    // to what the runner would have sent before.
    var sql = "CREATE TABLE x (id INT);\n-- a comment mentioning batch-begin in prose\nSELECT 1;";

    var segments = MigrationBatchRegions.Segment(sql);

    await Assert.That(segments.Count).IsEqualTo(1);
    await Assert.That(segments[0].Sql).IsEqualTo(sql);
  }

  [Test]
  public async Task NullSqlYieldsNoSegmentsRatherThanThrowingAsync() {
    // The runner calls this for every migration; a null body is a caller bug, not a migration fault,
    // and returning nothing lets the caller's own guard report it.
    var segments = MigrationBatchRegions.Segment(null!);

    await Assert.That(segments.Count).IsEqualTo(0);
  }

  [Test]
  public async Task ANestedRegionIsRejectedAsync() {
    // Two opens with no close between them would silently drop the first region's SQL into the
    // second, changing what runs.
    var sql = $"{MigrationBatchRegions.BEGIN}\nUPDATE a SET c = 1;\n{MigrationBatchRegions.BEGIN}\nUPDATE b SET c = 1;\n{MigrationBatchRegions.END}";

    await Assert.That(() => MigrationBatchRegions.Segment(sql)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task AnUnrecognizedSettingIsRejectedAsync() {
    // A typo such as 'rows=' would otherwise be ignored and the region would run at the default
    // size, which is the kind of silent difference that only shows up on the largest table.
    var sql = $"{MigrationBatchRegions.BEGIN} rows=500\nUPDATE t SET c = 1;\n{MigrationBatchRegions.END}";

    await Assert.That(() => MigrationBatchRegions.Segment(sql)).Throws<InvalidOperationException>();
  }

  [Test]
  public async Task SegmentsCompareByValueAsync() {
    // The runner logs and compares segments; value semantics keep those comparisons honest.
    var a = new MigrationSegment(MigrationSegmentKind.Batch, "SELECT 1;", 10);
    var b = new MigrationSegment(MigrationSegmentKind.Batch, "SELECT 1;", 10);
    var different = a with { BatchSize = 20 };
    var asPlain = a with { Kind = MigrationSegmentKind.Plain };
    var reworded = a with { Sql = "SELECT 2;" };

    await Assert.That(a).IsEqualTo(b);
    await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
    await Assert.That(a).IsNotEqualTo(different);
    await Assert.That(a.ToString()).Contains("Batch");
    await Assert.That(a != different).IsTrue();
    await Assert.That(a == b).IsTrue();

    var (kind, text, size) = a;
    await Assert.That(kind).IsEqualTo(MigrationSegmentKind.Batch);
    await Assert.That(text).IsEqualTo("SELECT 1;");
    await Assert.That(size).IsEqualTo(10);
    await Assert.That(a.Equals((object)b)).IsTrue();
    await Assert.That(asPlain.Kind).IsEqualTo(MigrationSegmentKind.Plain);
    await Assert.That(reworded.Sql).IsEqualTo("SELECT 2;");
  }

  /// <summary>Every shipped migration, as the runner reads them.</summary>
  private static IEnumerable<(string Name, string Sql)> _migrations() {
    var assembly = typeof(MigrationBatchRegions).Assembly;
    const string PREFIX = "Whizbang.Data.Postgres.Migrations.";

    foreach (var resource in assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(PREFIX, StringComparison.Ordinal)
                 && n.EndsWith(".sql", StringComparison.Ordinal))
        .OrderBy(n => n, StringComparer.Ordinal)) {
      using var stream = assembly.GetManifestResourceStream(resource)!;
      using var reader = new StreamReader(stream);
      yield return (resource[PREFIX.Length..], reader.ReadToEnd());
    }
  }

  [Test]
  public async Task EveryShippedMigrationSegmentsCleanlyAsync() {
    // A malformed marker throws, and a migration that throws here cannot start a service. This is
    // the cheapest place to find that out.
    var all = _migrations().ToList();
    await Assert.That(all.Count).IsGreaterThan(100);

    foreach (var (name, sql) in all) {
      try {
        var segments = MigrationBatchRegions.Segment(sql);
        await Assert.That(segments.Count).IsGreaterThan(0);
      } catch (InvalidOperationException ex) {
        throw new InvalidOperationException($"Migration {name} does not segment: {ex.Message}", ex);
      }
    }
  }

  [Test]
  public async Task TheMigrationsThatRewriteWholeTablesAreBatchedAsync() {
    // These five carry the only migration-time writes that scale with a consumer's data. Each one
    // reached this list by stalling, or by being the same shape as one that did, so losing a marker
    // here is a regression that only shows up on the largest database someone runs.
    string[] mustBatch = [
      "008_CreateMessageAssociationRegistry.sql",
      "046_CommitSequenceSchema.sql",
      "063_NormalizeClrTypeNamesV2.sql",
      "077_FullBodySplit.sql",
      "162_InboxWorkStateSideTable.sql",
    ];

    var byName = _migrations().ToDictionary(m => m.Name, m => m.Sql, StringComparer.Ordinal);

    foreach (var name in mustBatch) {
      await Assert.That(byName.ContainsKey(name)).IsTrue();
      var segments = MigrationBatchRegions.Segment(byName[name]);
      var batches = segments.Where(x => x.Kind == MigrationSegmentKind.Batch).ToList();

      await Assert.That(batches.Count).IsGreaterThanOrEqualTo(1);
      // A batched region reports a count the runner loops on, so it has to be a SELECT of a
      // function rather than bare DML, whose row count cannot carry a guard's "nothing to do".
      foreach (var b in batches) {
        await Assert.That(b.Sql).Contains("SELECT");
        await Assert.That(b.Sql).DoesNotContain(MigrationBatchRegions.SIZE_TOKEN);
        await Assert.That(b.BatchSize).IsGreaterThan(0);
      }
    }
  }

  [Test]
  public async Task NoShippedMigrationLeavesASizeTokenUnsubstitutedAsync() {
    // The token only means anything inside a region; one left in plain SQL would reach the database
    // verbatim and fail as a syntax error at migration time.
    foreach (var (_, sql) in _migrations()) {
      foreach (var segment in MigrationBatchRegions.Segment(sql)) {
        await Assert.That(segment.Sql.Contains(MigrationBatchRegions.SIZE_TOKEN, StringComparison.Ordinal))
          .IsFalse();
      }
    }
  }
}
