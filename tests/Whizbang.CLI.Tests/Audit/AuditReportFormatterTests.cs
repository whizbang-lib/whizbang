// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Whizbang.CLI.Audit;

namespace Whizbang.CLI.Tests.Audit;

/// <summary>
/// Tests for <see cref="AuditReportFormatter"/>: the table people read and the JSON tools read.
/// </summary>
/// <tests>Whizbang.CLI/Audit/AuditReportFormatter.cs</tests>
public class AuditReportFormatterTests {
  private const string CORE = "SoftwareExtravaganza.Whizbang.Core";
  private const string DATA = "SoftwareExtravaganza.Whizbang.Data.Postgres";
  private static readonly ResolvedPackage _core = new(CORE, "1.2.0");
  private static readonly ResolvedPackage _data = new(DATA, "1.2.0");

  private static AuditFinding _finding(
      string id = "GHSA-test-0001",
      string? cve = "CVE-2026-0001",
      AdvisorySeverity severity = AdvisorySeverity.High,
      string? summary = "Denial of service in the dispatcher",
      string? fixedVersion = "1.2.1",
      ResolvedPackage? package = null) =>
    new((package ?? _core).Id, (package ?? _core).Version, id, cve, severity, summary, fixedVersion);

  private static string _lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

  [Test]
  public async Task FormatText_NoFindings_SaysSoAndHowManyWereCheckedAsync() {
    var report = new AuditReport([_core, _data], [], AdvisorySeverity.Moderate);

    await Assert.That(AuditReportFormatter.FormatText(report)).IsEqualTo(_lines(
      "No published advisory affects the Whizbang packages this project uses (2 packages checked)."));
  }

  [Test]
  public async Task FormatText_NoFindingsInOnePackage_UsesTheSingularAsync() {
    var report = new AuditReport([_core], [], AdvisorySeverity.Moderate);

    await Assert.That(AuditReportFormatter.FormatText(report)).Contains("(1 package checked)");
  }

  [Test]
  public async Task FormatText_NoWhizbangPackages_SaysThereWasNothingToCheckAsync() {
    // "No advisory affects the packages" would be true and misleading: the project may simply be
    // the wrong one.
    var report = new AuditReport([], [], AdvisorySeverity.Moderate);

    await Assert.That(AuditReportFormatter.FormatText(report)).IsEqualTo(_lines(
      "This project uses no Whizbang packages (no SoftwareExtravaganza.Whizbang.* package in its restore output), so there was nothing to check."));
  }

  [Test]
  public async Task FormatText_Finding_IsATableThenDetailsThenTheVerdictAsync() {
    var report = new AuditReport([_core, _data], [_finding()], AdvisorySeverity.Moderate);

    await Assert.That(AuditReportFormatter.FormatText(report)).IsEqualTo(_lines(
      "Package                             Version  Advisory                        Severity  Upgrade to",
      "SoftwareExtravaganza.Whizbang.Core  1.2.0    GHSA-test-0001 (CVE-2026-0001)  high      1.2.1 or later",
      "",
      "GHSA-test-0001: Denial of service in the dispatcher",
      "  https://osv.dev/vulnerability/GHSA-test-0001",
      "",
      "1 advisory affects the Whizbang packages this project uses (2 packages checked). 1 at or above moderate: the audit fails."));
  }

  [Test]
  public async Task FormatText_FindingWithoutCveFixOrSummary_SaysWhatIsMissingAsync() {
    var report = new AuditReport([_core], [_finding(cve: null, summary: null, fixedVersion: null, severity: AdvisorySeverity.Unknown)], AdvisorySeverity.Moderate);

    var text = AuditReportFormatter.FormatText(report);

    await Assert.That(text).Contains("GHSA-test-0001  ");
    await Assert.That(text).DoesNotContain("(CVE");
    await Assert.That(text).Contains("unknown   no fixed version published");
    await Assert.That(text).Contains(_lines("", "GHSA-test-0001", "  https://osv.dev/vulnerability/GHSA-test-0001"));
  }

  [Test]
  public async Task FormatText_SeveralFindingsNoneFailing_SaysNoneReachesTheThresholdAsync() {
    var report = new AuditReport(
      [_core, _data],
      [_finding(severity: AdvisorySeverity.Low), _finding(id: "GHSA-test-0002", severity: AdvisorySeverity.Moderate, package: _data)],
      AdvisorySeverity.High);

    var text = AuditReportFormatter.FormatText(report);

    await Assert.That(text).Contains("2 advisories affect the Whizbang packages this project uses (2 packages checked). None at or above high.");
    await Assert.That(text).Contains("SoftwareExtravaganza.Whizbang.Data.Postgres  1.2.0");
  }

  [Test]
  public async Task FormatText_FailOnNone_SaysTheAuditDoesNotFailAsync() {
    var report = new AuditReport([_core], [_finding(severity: AdvisorySeverity.Critical)], FailOn: null);

    await Assert.That(AuditReportFormatter.FormatText(report))
      .Contains("(1 package checked). --fail-on none: reported only, the audit does not fail.");
  }

  [Test]
  public async Task FormatJson_CarriesEveryFieldOfEveryFindingAsync() {
    var report = new AuditReport([_core, _data], [_finding()], AdvisorySeverity.Moderate);

    var json = JsonDocument.Parse(AuditReportFormatter.FormatJson(report)).RootElement;

    await Assert.That(json.GetProperty("packagesChecked").GetInt32()).IsEqualTo(2);
    await Assert.That(json.GetProperty("failOn").GetString()).IsEqualTo("moderate");
    await Assert.That(json.GetProperty("fails").GetBoolean()).IsTrue();
    await Assert.That(json.GetProperty("packages")[1].GetProperty("id").GetString()).IsEqualTo(DATA);
    await Assert.That(json.GetProperty("packages")[1].GetProperty("version").GetString()).IsEqualTo("1.2.0");
    var finding = json.GetProperty("findings")[0];
    await Assert.That(finding.GetProperty("package").GetString()).IsEqualTo(CORE);
    await Assert.That(finding.GetProperty("version").GetString()).IsEqualTo("1.2.0");
    await Assert.That(finding.GetProperty("advisoryId").GetString()).IsEqualTo("GHSA-test-0001");
    await Assert.That(finding.GetProperty("cve").GetString()).IsEqualTo("CVE-2026-0001");
    await Assert.That(finding.GetProperty("severity").GetString()).IsEqualTo("high");
    await Assert.That(finding.GetProperty("summary").GetString()).IsEqualTo("Denial of service in the dispatcher");
    await Assert.That(finding.GetProperty("fixedVersion").GetString()).IsEqualTo("1.2.1");
    await Assert.That(finding.GetProperty("url").GetString()).IsEqualTo("https://osv.dev/vulnerability/GHSA-test-0001");
  }

  [Test]
  public async Task FormatJson_MissingValues_AreExplicitNullsAsync() {
    // A tool reading the report should find every key on every finding, not have to tell a
    // missing CVE apart from a renamed field.
    var report = new AuditReport([_core], [_finding(cve: null, summary: null, fixedVersion: null)], FailOn: null);

    var json = JsonDocument.Parse(AuditReportFormatter.FormatJson(report)).RootElement;

    await Assert.That(json.GetProperty("failOn").GetString()).IsEqualTo("none");
    await Assert.That(json.GetProperty("fails").GetBoolean()).IsFalse();
    var finding = json.GetProperty("findings")[0];
    await Assert.That(finding.GetProperty("cve").ValueKind).IsEqualTo(JsonValueKind.Null);
    await Assert.That(finding.GetProperty("summary").ValueKind).IsEqualTo(JsonValueKind.Null);
    await Assert.That(finding.GetProperty("fixedVersion").ValueKind).IsEqualTo(JsonValueKind.Null);
  }
}
