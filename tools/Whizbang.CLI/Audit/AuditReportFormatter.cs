// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.CLI.Audit;

/// <summary>
/// Renders an <see cref="AuditReport"/> as a table for people or as JSON for tools.
/// </summary>
/// <docs>tools/cli-audit#output</docs>
internal static class AuditReportFormatter {
  private const string COLUMN_GAP = "  ";

  /// <summary>Renders the report as a table followed by a one-line verdict.</summary>
  /// <param name="report">The report.</param>
  /// <returns>The text, ending in a newline.</returns>
  public static string FormatText(AuditReport report) {
    var text = new StringBuilder();
    var checkedCount = _count(report.Packages.Count, "package", "packages");
    if (report.Packages.Count == 0) {
      text.AppendLine(
        CultureInfo.InvariantCulture, $"This project uses no Whizbang packages (no {ProjectAssetsReader.PACKAGE_PREFIX}* package in its restore output), so there was nothing to check.");
      return text.ToString();
    }

    if (report.Findings.Count == 0) {
      text.AppendLine(CultureInfo.InvariantCulture, $"No published advisory affects the Whizbang packages this project uses ({checkedCount} checked).");
      return text.ToString();
    }

    _appendTable(text, report.Findings);
    foreach (var finding in report.Findings) {
      text.AppendLine();
      text.AppendLine(finding.Summary is null ? finding.AdvisoryId : $"{finding.AdvisoryId}: {finding.Summary}");
      text.AppendLine(CultureInfo.InvariantCulture, $"  {finding.Url}");
    }

    text.AppendLine();
    var affects = report.Findings.Count == 1 ? "1 advisory affects" : $"{report.Findings.Count} advisories affect";
    text.AppendLine(CultureInfo.InvariantCulture, $"{affects} the Whizbang packages this project uses ({checkedCount} checked). {_verdict(report)}");
    return text.ToString();
  }

  /// <summary>Renders the report as JSON.</summary>
  /// <param name="report">The report.</param>
  /// <returns>The JSON document.</returns>
  public static string FormatJson(AuditReport report) {
    var json = new AuditJsonReport {
      PackagesChecked = report.Packages.Count,
      FailOn = _thresholdWord(report.FailOn),
      Fails = report.Failing.Count > 0,
      Packages = [.. report.Packages],
      Findings = [.. report.Findings.Select(f => new AuditJsonFinding {
        Package = f.PackageId,
        Version = f.Version,
        AdvisoryId = f.AdvisoryId,
        Cve = f.Cve,
        Severity = AdvisorySeverities.ToWord(f.Severity),
        Summary = f.Summary,
        FixedVersion = f.FixedVersion,
        Url = f.Url,
      })],
    };
    return JsonSerializer.Serialize(json, AuditJsonContext.Default.AuditJsonReport);
  }

  private static void _appendTable(StringBuilder text, IReadOnlyList<AuditFinding> findings) {
    string[] header = ["Package", "Version", "Advisory", "Severity", "Upgrade to"];
    var rows = findings.Select(f => new[] {
      f.PackageId,
      f.Version,
      f.Cve is null ? f.AdvisoryId : $"{f.AdvisoryId} ({f.Cve})",
      AdvisorySeverities.ToWord(f.Severity),
      f.FixedVersion is null ? "no fixed version published" : $"{f.FixedVersion} or later",
    }).Prepend(header).ToList();
    var widths = Enumerable.Range(0, header.Length).Select(c => rows.Max(r => r[c].Length)).ToArray();
    foreach (var row in rows) {
      text.AppendLine(string.Join(COLUMN_GAP, row.Select((cell, c) => cell.PadRight(widths[c]))).TrimEnd());
    }
  }

  private static string _verdict(AuditReport report) {
    if (report.FailOn is not { } threshold) {
      return "--fail-on none: reported only, the audit does not fail.";
    }

    var failing = report.Failing.Count;
    var word = AdvisorySeverities.ToWord(threshold);
    return failing == 0
      ? $"None at or above {word}."
      : $"{failing.ToString(CultureInfo.InvariantCulture)} at or above {word}: the audit fails.";
  }

  private static string _thresholdWord(AdvisorySeverity? threshold) =>
    threshold is { } t ? AdvisorySeverities.ToWord(t) : "none";

  private static string _count(int count, string singular, string plural) =>
    $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";
}

/// <summary>The <c>--json</c> report.</summary>
internal sealed class AuditJsonReport {
  /// <summary>How many Whizbang package versions were checked.</summary>
  public int PackagesChecked { get; init; }

  /// <summary>The threshold: low, moderate, high, critical or none.</summary>
  public required string FailOn { get; init; }

  /// <summary>Whether any finding is at or above the threshold (exit code 1).</summary>
  public bool Fails { get; init; }

  /// <summary>Every package version checked.</summary>
  public List<ResolvedPackage> Packages { get; set; } = [];

  /// <summary>Every advisory affecting one of them.</summary>
  public List<AuditJsonFinding> Findings { get; set; } = [];
}

/// <summary>One finding in the <c>--json</c> report. Every key is always present; a missing value is null.</summary>
internal sealed class AuditJsonFinding {
  /// <summary>The affected package.</summary>
  public required string Package { get; init; }

  /// <summary>The version the project resolves.</summary>
  public required string Version { get; init; }

  /// <summary>The advisory id.</summary>
  public required string AdvisoryId { get; init; }

  /// <summary>The CVE, or null when none was assigned.</summary>
  [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
  public string? Cve { get; init; }

  /// <summary>low, moderate, high, critical or unknown.</summary>
  public required string Severity { get; init; }

  /// <summary>One line describing the advisory, or null.</summary>
  [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
  public string? Summary { get; init; }

  /// <summary>The nearest fixed version above the resolved one, or null when none is published.</summary>
  [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
  public string? FixedVersion { get; init; }

  /// <summary>The advisory's page on osv.dev.</summary>
  public required string Url { get; init; }
}
