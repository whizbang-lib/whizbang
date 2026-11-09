// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.TestFixtures;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The core comparison of the hash upgrade: which recorded hashes are an earlier release's record of a migration that
/// has not changed since (#2125). Checked against the hashes v0.2623.1 really recorded, captured from its packages.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/MigrationSqlNormalizer.cs</code-under-test>
/// <docs>operations/infrastructure/migrations#what-counts-as-a-change</docs>
[Category("Unit")]
public class LegacyMigrationHashRecognitionTests {
  // REUSE-IgnoreStart: the header written into fixture text, not this file's license
  private const string HEADER_LF = "-- Copyright (c) whizbang-lib contributors.\n-- SPDX-License-Identifier: MIT\n\n";
  private const string HEADER_CRLF = "-- Copyright (c) whizbang-lib contributors.\r\n-- SPDX-License-Identifier: MIT\r\n\r\n";
  // REUSE-IgnoreEnd
  private const string BODY = "-- a note\nCREATE TABLE IF NOT EXISTS wh_probe (id int);\n";

  private static Dictionary<string, string> _todaysTexts() =>
    new PostgresMigrationProvider(typeof(PostgresMigrationProvider).Assembly, "public").GetMigrations()
      .ToDictionary(m => m.Name, m => m.Sql, StringComparer.Ordinal);

  /// <summary>Every hash v0.2623.1 recorded is recognized from today's text of the same file.</summary>
  [Test]
  public async Task IsLegacyContentHash_EveryHashV0_2623_1Recorded_IsRecognizedFromTodaysTextAsync() {
    var today = _todaysTexts();
    var ledger = LegacyMigrationLedger.Read(LegacyMigrationLedger.V0_2623_1, "dapper-public");
    await Assert.That(ledger.Count).IsEqualTo(204);

    var unrecognized = ledger.Where(row => !MigrationSqlNormalizer.IsLegacyContentHash(row.Hash, today[row.FileName]))
      .Select(row => row.FileName).ToList();

    await Assert.That(unrecognized).IsEmpty();
  }

  /// <summary>A file whose content really changed since the release that recorded it is not recognized.</summary>
  [Test]
  public async Task IsLegacyContentHash_AFileThatReallyChanged_IsNotRecognizedAsync() {
    const string CHANGED = "204_DirectInstancesAreLiveByTheirAliveLock";
    var (_, olderHash) = LegacyMigrationLedger.Read(LegacyMigrationLedger.V0_2619_0, "dapper-public").Single(r => r.FileName == CHANGED);

    await Assert.That(MigrationSqlNormalizer.IsLegacyContentHash(olderHash, _todaysTexts()[CHANGED])).IsFalse();
  }

  /// <summary>No row is no recorded hash.</summary>
  [Test]
  public async Task IsLegacyContentHash_NoRecordedHash_IsFalseAsync() =>
    await Assert.That(MigrationSqlNormalizer.IsLegacyContentHash(null, BODY)).IsFalse();

  /// <summary>The raw-text hash of the text as it is now stays recognized.</summary>
  [Test]
  public async Task LegacyContentHashes_IncludeTheRawHashOfTheTextAsItIsAsync() {
    var hashes = MigrationSqlNormalizer.LegacyContentHashes(HEADER_LF + BODY);

    await Assert.That(hashes).Contains(MigrationSqlNormalizer.LegacyContentHash(HEADER_LF + BODY));
    await Assert.That(hashes).Contains(MigrationSqlNormalizer.LegacyContentHash(BODY));
    await Assert.That(hashes.Count).IsEqualTo(2);
  }

  /// <summary>The header is recognized with CRLF line endings too, as a package packed from a CRLF checkout has it.</summary>
  [Test]
  public async Task LegacyContentHashes_AHeaderWithCrlfLineEndings_IsStrippedAsync() {
    var hashes = MigrationSqlNormalizer.LegacyContentHashes(HEADER_CRLF + BODY);

    await Assert.That(hashes).Contains(MigrationSqlNormalizer.LegacyContentHash(BODY));
  }

  /// <summary>Text with no header has one raw form, listed once.</summary>
  [Test]
  public async Task LegacyContentHashes_TextWithoutTheHeader_HasOneFormAsync() =>
    await Assert.That(MigrationSqlNormalizer.LegacyContentHashes(BODY))
      .IsEquivalentTo([MigrationSqlNormalizer.LegacyContentHash(BODY)]);

  /// <summary>Only a header at the start is the license header; the same lines later are content.</summary>
  [Test]
  public async Task LegacyContentHashes_TheHeaderLinesAfterTheStart_AreNotStrippedAsync() {
    var text = BODY + HEADER_LF;

    await Assert.That(MigrationSqlNormalizer.LegacyContentHashes(text))
      .IsEquivalentTo([MigrationSqlNormalizer.LegacyContentHash(text)]);
  }

  /// <summary>
  /// The EF Core generator indents every embedded line after the first: the header's second line and the line that was
  /// first before the header carry the indentation, which the earlier release's first line did not.
  /// </summary>
  [Test]
  public async Task LegacyContentHashes_AnIndentedEmbedding_StripsTheIndentationOfTheFormerFirstLineAsync() {
    const string EMBEDDED = "-- Copyright (c) whizbang-lib contributors.\n      -- SPDX-License-Identifier: MIT\n\n      SELECT 1;\n      SELECT 2;";

    await Assert.That(MigrationSqlNormalizer.LegacyContentHashes(EMBEDDED))
      .Contains(MigrationSqlNormalizer.LegacyContentHash("SELECT 1;\n      SELECT 2;"));
  }

  /// <summary>Lone carriage returns end lines too, and a tab is indentation, as the normalizer reads them.</summary>
  [Test]
  public async Task LegacyContentHashes_LoneCarriageReturnsAndTabIndentation_AreStrippedAsync() {
    const string EMBEDDED = "-- Copyright (c) whizbang-lib contributors.\r\t-- SPDX-License-Identifier: MIT\r\r\tSELECT 1;";

    await Assert.That(MigrationSqlNormalizer.LegacyContentHashes(EMBEDDED))
      .Contains(MigrationSqlNormalizer.LegacyContentHash("SELECT 1;"));
  }

  /// <summary>An indented embedding whose former first line was blank keeps it blank.</summary>
  [Test]
  public async Task LegacyContentHashes_AnIndentedEmbeddingWithABlankFirstLine_KeepsItAsync() {
    const string EMBEDDED = "-- Copyright (c) whizbang-lib contributors.\n      -- SPDX-License-Identifier: MIT\n\n\n      SELECT 1;";

    await Assert.That(MigrationSqlNormalizer.LegacyContentHashes(EMBEDDED))
      .Contains(MigrationSqlNormalizer.LegacyContentHash("\n      SELECT 1;"));
  }

  /// <summary>Text that only starts like the header is not stripped: each part of it is required.</summary>
  [Test]
  [Arguments("-- Copyright (c) whizbang-lib contributors.")]
  [Arguments("-- Copyright (c) whizbang-lib contributors.\n-- something else\n\nSELECT 1;")]
  [Arguments("-- Copyright (c) whizbang-lib contributors.\n-- SPDX-License-Identifier: MIT")]
  [Arguments("-- Copyright (c) whizbang-lib contributors.\n-- SPDX-License-Identifier: MIT\nSELECT 1;")]
  [Arguments("-- Copyright (c) whizbang-lib contributors. SELECT 1;")]
  [Arguments("-- Copyright (c) whizbang-lib contributors.\r")]
  public async Task LegacyContentHashes_APartialHeader_IsNotStrippedAsync(string text) =>
    await Assert.That(MigrationSqlNormalizer.LegacyContentHashes(text))
      .IsEquivalentTo([MigrationSqlNormalizer.LegacyContentHash(text)]);

  /// <summary>The text an earlier driver rendered adds its own two forms; a form already listed is not repeated.</summary>
  [Test]
  public async Task LegacyContentHashes_WithEarlierReleaseText_AddsItsFormsAsync() {
    const string EARLIER = "-- a note\nCREATE TABLE IF NOT EXISTS \"svc\".wh_probe (id int);\n";

    var hashes = MigrationSqlNormalizer.LegacyContentHashes(HEADER_LF + BODY, HEADER_LF + EARLIER);
    var same = MigrationSqlNormalizer.LegacyContentHashes(HEADER_LF + BODY, HEADER_LF + BODY);

    await Assert.That(hashes).IsEquivalentTo([
      MigrationSqlNormalizer.LegacyContentHash(HEADER_LF + BODY),
      MigrationSqlNormalizer.LegacyContentHash(BODY),
      MigrationSqlNormalizer.LegacyContentHash(HEADER_LF + EARLIER),
      MigrationSqlNormalizer.LegacyContentHash(EARLIER)]);
    await Assert.That(same.Count).IsEqualTo(2);
  }

  /// <summary>A recorded hash of the earlier driver's text is recognized.</summary>
  [Test]
  public async Task IsLegacyContentHash_TheEarlierDriversText_IsRecognizedAsync() {
    const string EARLIER = "CREATE TABLE \"svc\".wh_probe (id int);";

    await Assert.That(MigrationSqlNormalizer.IsLegacyContentHash(
      MigrationSqlNormalizer.LegacyContentHash(EARLIER), BODY, EARLIER)).IsTrue();
  }

  /// <summary>The text is required.</summary>
  [Test]
  public async Task LegacyContentHashes_NullText_ThrowsAsync() =>
    await Assert.That(() => MigrationSqlNormalizer.LegacyContentHashes(null!)).Throws<ArgumentNullException>();
}
