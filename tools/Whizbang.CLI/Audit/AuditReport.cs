// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.CLI.Audit;

/// <summary>
/// One published advisory affecting one resolved package version.
/// </summary>
/// <param name="PackageId">The affected package.</param>
/// <param name="Version">The version the project resolves.</param>
/// <param name="AdvisoryId">The advisory id, such as a GHSA identifier.</param>
/// <param name="Cve">The CVE assigned to the advisory, when there is one.</param>
/// <param name="Severity">The advisory's severity.</param>
/// <param name="Summary">One line describing the advisory, when the record has one.</param>
/// <param name="FixedVersion">The nearest version above <paramref name="Version"/> that fixes it, when one is published.</param>
internal sealed record AuditFinding(
  string PackageId,
  string Version,
  string AdvisoryId,
  string? Cve,
  AdvisorySeverity Severity,
  string? Summary,
  string? FixedVersion) {
  /// <summary>The advisory's page on osv.dev.</summary>
  public string Url => $"https://osv.dev/vulnerability/{Uri.EscapeDataString(AdvisoryId)}";
}

/// <summary>
/// The result of an audit: what was checked, what affects it, and whether that fails the audit.
/// </summary>
/// <param name="Packages">Every Whizbang package version checked.</param>
/// <param name="Findings">Every advisory affecting one of them.</param>
/// <param name="FailOn">The <c>--fail-on</c> threshold, or null for <c>none</c>.</param>
/// <docs>tools/cli-audit</docs>
internal sealed record AuditReport(
  IReadOnlyList<ResolvedPackage> Packages,
  IReadOnlyList<AuditFinding> Findings,
  AdvisorySeverity? FailOn) {
  private static readonly Comparer<string> _versionOrder = Comparer<string>.Create(NuGetVersionOrder.Compare);

  /// <summary>The findings at or above the threshold.</summary>
  public IReadOnlyList<AuditFinding> Failing => [.. Findings.Where(f => AdvisorySeverities.Fails(f.Severity, FailOn))];

  /// <summary>
  /// Builds the report from OSV's matches.
  /// </summary>
  /// <param name="packages">Every package version checked.</param>
  /// <param name="matches">The advisories OSV reported for them.</param>
  /// <param name="failOn">The threshold, or null for <c>none</c>.</param>
  /// <returns>The report, findings ordered by package, version and advisory.</returns>
  public static AuditReport Build(IReadOnlyList<ResolvedPackage> packages, IReadOnlyList<OsvMatch> matches, AdvisorySeverity? failOn) {
    var findings = matches
      .Select(m => new AuditFinding(
        m.Package.Id,
        m.Package.Version,
        m.Advisory.Id,
        m.Advisory.Aliases.FirstOrDefault(a => a.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase)),
        AdvisorySeverities.FromOsv(m.Advisory.DatabaseSpecific?.Severity),
        m.Advisory.Summary,
        _nearestFix(m.Package, m.Advisory)))
      .OrderBy(f => f.PackageId, StringComparer.OrdinalIgnoreCase)
      .ThenBy(f => f.Version, _versionOrder)
      .ThenBy(f => f.AdvisoryId, StringComparer.Ordinal);
    return new AuditReport(packages, [.. findings], failOn);
  }

  // The fix for this package only: one advisory can list several packages, and a fixed event on
  // another package's entry says nothing about this one. Of the fixed versions above the resolved
  // one, the nearest is the smallest upgrade that leaves the affected range. A commit range names
  // commits, not versions.
  private static string? _nearestFix(ResolvedPackage package, OsvVulnerability advisory) =>
    advisory.Affected
      .Where(a => a.Package is { } p
        && string.Equals(p.Ecosystem, "NuGet", StringComparison.OrdinalIgnoreCase)
        && string.Equals(p.Name, package.Id, StringComparison.OrdinalIgnoreCase))
      .SelectMany(a => a.Ranges)
      .Where(r => !string.Equals(r.Type, "GIT", StringComparison.OrdinalIgnoreCase))
      .SelectMany(r => r.Events)
      .Select(e => e.Fixed)
      .OfType<string>()
      .Where(f => NuGetVersionOrder.Compare(f, package.Version) > 0)
      .Order(_versionOrder)
      .FirstOrDefault();
}
