// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Whizbang.CLI.Audit;

namespace Whizbang.CLI.Tests.Audit;

/// <summary>
/// Tests for <see cref="AuditReport.Build"/>: turning OSV advisory records into findings with a
/// CVE, a severity and the version to upgrade to, and the threshold that fails the audit.
/// </summary>
/// <tests>Whizbang.CLI/Audit/AuditReport.cs</tests>
public class AuditReportTests {
  private const string CORE = "SoftwareExtravaganza.Whizbang.Core";
  private static readonly ResolvedPackage _core = new(CORE, "1.2.0");

  internal static OsvVulnerability Record(string json) =>
    JsonSerializer.Deserialize(json, AuditJsonContext.Default.OsvVulnerability)!;

  private static AuditFinding _single(string recordJson, ResolvedPackage? package = null) {
    var report = AuditReport.Build([package ?? _core], [new OsvMatch(package ?? _core, Record(recordJson))], AdvisorySeverity.Moderate);
    return report.Findings.Single();
  }

  [Test]
  public async Task Build_Finding_CarriesTheAdvisoryCveSeverityAndSummaryAsync() {
    var finding = _single(StubOsvHandler.Advisory("GHSA-test-0001", CORE, "HIGH", "CVE-2026-0001", ["1.2.1"]));

    await Assert.That(finding).IsEqualTo(new AuditFinding(
      CORE, "1.2.0", "GHSA-test-0001", "CVE-2026-0001", AdvisorySeverity.High, "Summary of GHSA-test-0001", "1.2.1"));
    await Assert.That(finding.Url).IsEqualTo("https://osv.dev/vulnerability/GHSA-test-0001");
  }

  [Test]
  [Arguments("8.0.4", "8.0.5")]
  [Arguments("6.0.9", "6.0.10")]
  public async Task Build_RealOsvRecord_ReadsCveSeverityAndTheFixForTheResolvedLineAsync(string resolved, string expectedFix) {
    // A GitHub-reviewed advisory exactly as api.osv.dev serves it (details trimmed): distribution
    // aliases before the CVE, severity under database_specific, and the same package listed twice,
    // once per fixed line. The command filters to Whizbang packages before it asks, but the record
    // shape is the same for every NuGet package, so a published one pins the parsing.
    var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Audit", "Fixtures", "GHSA-8g4q-xg66-9fp4.osv.json"));
    var package = new ResolvedPackage("System.Text.Json", resolved);

    var finding = _single(json, package);

    await Assert.That(finding.Cve).IsEqualTo("CVE-2024-43485");
    await Assert.That(finding.Severity).IsEqualTo(AdvisorySeverity.High);
    await Assert.That(finding.FixedVersion).IsEqualTo(expectedFix);
    await Assert.That(finding.Summary).StartsWith("Microsoft Security Advisory CVE-2024-43485");
  }

  [Test]
  public async Task Build_CveAmongOtherAliases_IsPickedOutAsync() {
    // Real records list distribution ids before the CVE (BIT-..., then CVE-...).
    var record = StubOsvHandler.Advisory("GHSA-test-0002", CORE)
      .Replace("\"aliases\": []", "\"aliases\": [\"BIT-dotnet-2026-0002\", \"CVE-2026-0002\"]", StringComparison.Ordinal);

    await Assert.That(_single(record).Cve).IsEqualTo("CVE-2026-0002");
  }

  [Test]
  public async Task Build_RecordWithoutAliasesOrSeverity_HasNoCveAndAnUnknownSeverityAsync() {
    // OSV leaves out "aliases" when a GHSA has no CVE yet, and "database_specific" for some
    // sources. Neither may stop the report, and neither may be read as "low".
    var record = """
      { "id": "GHSA-test-0003", "affected": [
        { "package": { "name": "SoftwareExtravaganza.Whizbang.Core", "ecosystem": "NuGet" },
          "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "1.3.0" } ] } ] } ] }
      """;

    var finding = _single(record);

    await Assert.That(finding.Cve).IsNull();
    await Assert.That(finding.Severity).IsEqualTo(AdvisorySeverity.Unknown);
    await Assert.That(finding.Summary).IsNull();
  }

  [Test]
  public async Task Build_SeveralFixedLines_PicksTheNearestAboveTheResolvedVersionAsync() {
    // An advisory fixed on two lines (1.1.5 for 1.1.x, 1.2.3 for 1.2.x, and 2.0.0) is fixed for
    // a 1.2.0 user by 1.2.3: 1.1.5 is a downgrade and 2.0.0 a bigger move than needed.
    var finding = _single(StubOsvHandler.Advisory("GHSA-test-0004", CORE, fixedVersions: ["2.0.0", "1.1.5", "1.2.3"]));

    await Assert.That(finding.FixedVersion).IsEqualTo("1.2.3");
  }

  [Test]
  public async Task Build_NoFixedVersion_LeavesItEmptyAsync() {
    var record = StubOsvHandler.Advisory("GHSA-test-0005", CORE)
      .Replace("{\"fixed\":\"9.9.9\"}", "{\"last_affected\":\"1.2.0\"}", StringComparison.Ordinal);

    await Assert.That(_single(record).FixedVersion).IsNull();
  }

  [Test]
  public async Task Build_OtherPackagesInTheRecord_DoNotSupplyTheFixedVersionAsync() {
    // One advisory can cover several packages. The upgrade target must come from the entry for
    // the package that is affected here, not from a non-Whizbang package listed beside it, nor
    // from a same-named package in another ecosystem, nor from an entry with no package at all.
    const string others = """
      { "package": { "name": "Some.Other.Package", "ecosystem": "NuGet" },
        "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "1.2.1" } ] } ] },
      { "package": { "name": "SoftwareExtravaganza.Whizbang.Core", "ecosystem": "npm" },
        "ranges": [ { "type": "SEMVER", "events": [ { "introduced": "0" }, { "fixed": "1.2.2" } ] } ] },
      { "ranges": [ { "type": "ECOSYSTEM", "events": [ { "introduced": "0" }, { "fixed": "1.2.3" } ] } ] },
      """;

    var finding = _single(StubOsvHandler.Advisory("GHSA-test-0006", CORE, fixedVersions: ["1.4.0"], extraAffected: others));

    await Assert.That(finding.FixedVersion).IsEqualTo("1.4.0");
  }

  [Test]
  public async Task Build_PackageNameCase_DoesNotMatterAsync() {
    // NuGet ids are case-insensitive; a record may spell the id differently from the assets file.
    var finding = _single(StubOsvHandler.Advisory("GHSA-test-0007", "softwareextravaganza.whizbang.core", fixedVersions: ["1.2.5"]));

    await Assert.That(finding.FixedVersion).IsEqualTo("1.2.5");
  }

  [Test]
  public async Task Build_CommitRange_IsNotAVersionToUpgradeToAsync() {
    var record = StubOsvHandler.Advisory("GHSA-test-0008", CORE, fixedVersions: ["1.2.6"])
      .Replace("\"type\":\"ECOSYSTEM\"", "\"type\":\"GIT\"", StringComparison.Ordinal);

    await Assert.That(_single(record).FixedVersion).IsNull();
  }

  [Test]
  public async Task Build_Findings_AreOrderedByPackageVersionAndAdvisoryAsync() {
    var data = new ResolvedPackage("SoftwareExtravaganza.Whizbang.Data", "1.0.0");
    var older = new ResolvedPackage(CORE, "1.1.0");
    var matches = new[] {
      new OsvMatch(data, Record(StubOsvHandler.Advisory("GHSA-a", data.Id))),
      new OsvMatch(_core, Record(StubOsvHandler.Advisory("GHSA-c", CORE))),
      new OsvMatch(_core, Record(StubOsvHandler.Advisory("GHSA-b", CORE))),
      new OsvMatch(older, Record(StubOsvHandler.Advisory("GHSA-d", CORE))),
    };

    var report = AuditReport.Build([older, _core, data], matches, AdvisorySeverity.Moderate);

    await Assert.That(string.Join(",", report.Findings.Select(f => f.AdvisoryId))).IsEqualTo("GHSA-d,GHSA-b,GHSA-c,GHSA-a");
  }

  [Test]
  [Arguments("LOW", "Moderate", 0)]
  [Arguments("MODERATE", "Moderate", 1)]
  [Arguments("HIGH", "Critical", 0)]
  [Arguments("CRITICAL", "Critical", 1)]
  public async Task Failing_IsTheFindingsAtOrAboveTheThresholdAsync(string severity, string failOn, int expected) {
    var report = AuditReport.Build(
      [_core],
      [new OsvMatch(_core, Record(StubOsvHandler.Advisory("GHSA-test-0009", CORE, severity)))],
      Enum.Parse<AdvisorySeverity>(failOn));

    await Assert.That(report.Failing.Count).IsEqualTo(expected);
  }

  [Test]
  public async Task Failing_FailOnNone_IsEmptyEvenForACriticalAdvisoryAsync() {
    var report = AuditReport.Build(
      [_core],
      [new OsvMatch(_core, Record(StubOsvHandler.Advisory("GHSA-test-0010", CORE, "CRITICAL")))],
      failOn: null);

    await Assert.That(report.Findings).Count().IsEqualTo(1);
    await Assert.That(report.Failing).IsEmpty();
  }
}
