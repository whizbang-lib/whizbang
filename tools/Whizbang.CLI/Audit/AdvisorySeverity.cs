// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.CLI.Audit;

/// <summary>
/// The severity an advisory database assigns to a published advisory, in ascending order.
/// </summary>
/// <docs>tools/cli-audit</docs>
internal enum AdvisorySeverity {
  /// <summary>Low severity.</summary>
  Low = 1,
  /// <summary>Moderate severity (some databases call it medium).</summary>
  Moderate = 2,
  /// <summary>High severity.</summary>
  High = 3,
  /// <summary>Critical severity.</summary>
  Critical = 4,
  /// <summary>The record carries no severity the command recognizes.</summary>
  Unknown = 5,
}

/// <summary>
/// Reads severities from advisory records and the <c>--fail-on</c> option, and decides whether an
/// advisory reaches the failure threshold.
/// </summary>
/// <docs>tools/cli-audit#exit-codes</docs>
internal static class AdvisorySeverities {
  /// <summary>
  /// Reads the severity an OSV record carries in <c>database_specific.severity</c>.
  /// </summary>
  /// <param name="value">The raw value, or null when the record has none.</param>
  /// <returns>The severity, or <see cref="AdvisorySeverity.Unknown"/> when absent or unrecognized.</returns>
  public static AdvisorySeverity FromOsv(string? value) => value?.ToUpperInvariant() switch {
    "LOW" => AdvisorySeverity.Low,
    "MODERATE" or "MEDIUM" => AdvisorySeverity.Moderate,
    "HIGH" => AdvisorySeverity.High,
    "CRITICAL" => AdvisorySeverity.Critical,
    _ => AdvisorySeverity.Unknown,
  };

  /// <summary>
  /// Parses the value of <c>--fail-on</c>.
  /// </summary>
  /// <param name="value">low, moderate, high, critical or none (any case).</param>
  /// <param name="threshold">The threshold, or null for <c>none</c> (never fail on an advisory).</param>
  /// <returns>True when the value is one of the accepted words.</returns>
  public static bool TryParseThreshold(string value, out AdvisorySeverity? threshold) {
    threshold = value.ToUpperInvariant() switch {
      "LOW" => AdvisorySeverity.Low,
      "MODERATE" => AdvisorySeverity.Moderate,
      "HIGH" => AdvisorySeverity.High,
      "CRITICAL" => AdvisorySeverity.Critical,
      _ => null,
    };
    return threshold is not null || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Whether an advisory of <paramref name="severity"/> fails the audit at <paramref name="threshold"/>.
  /// </summary>
  /// <param name="severity">The advisory's severity.</param>
  /// <param name="threshold">The threshold, or null for <c>none</c>.</param>
  /// <returns>True when the advisory is at or above the threshold.</returns>
  /// <remarks>
  /// <see cref="AdvisorySeverity.Unknown"/> orders above <see cref="AdvisorySeverity.Critical"/>, so an
  /// advisory that affects the resolved version but carries no severity fails at every threshold:
  /// a missing field is not evidence of a harmless advisory.
  /// </remarks>
  public static bool Fails(AdvisorySeverity severity, AdvisorySeverity? threshold) =>
    threshold is { } floor && severity >= floor;

  /// <summary>
  /// The lower-case word used for a severity in the report and in <c>--fail-on</c>.
  /// </summary>
  /// <param name="severity">The severity.</param>
  /// <returns>low, moderate, high, critical or unknown.</returns>
  public static string ToWord(AdvisorySeverity severity) =>
#pragma warning disable CA1308 // The word is shown to people and matched case-insensitively, never round-tripped.
    severity.ToString().ToLowerInvariant();
#pragma warning restore CA1308
}
