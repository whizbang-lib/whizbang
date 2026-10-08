// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.CLI.Audit;

namespace Whizbang.CLI.Tests.Audit;

/// <summary>
/// Tests for <see cref="AdvisorySeverities"/>: reading an OSV record's severity, parsing
/// <c>--fail-on</c>, and the threshold decision that sets the exit code.
/// </summary>
/// <tests>Whizbang.CLI/Audit/AdvisorySeverity.cs</tests>
public class AdvisorySeveritiesTests {

  [Test]
  [Arguments("LOW", "Low")]
  [Arguments("MODERATE", "Moderate")]
  [Arguments("MEDIUM", "Moderate")]
  [Arguments("high", "High")]
  [Arguments("CRITICAL", "Critical")]
  [Arguments("SEVERE", "Unknown")]
  [Arguments(null, "Unknown")]
  public async Task FromOsv_MapsTheDatabaseWordAsync(string? value, string expected) {
    // GitHub-reviewed records say MODERATE; other OSV sources say MEDIUM. Both mean the same
    // thing to a threshold, and a word the command does not know must never read as "low".
    await Assert.That(AdvisorySeverities.FromOsv(value)).IsEqualTo(Enum.Parse<AdvisorySeverity>(expected));
  }

  [Test]
  [Arguments("low", "Low")]
  [Arguments("Moderate", "Moderate")]
  [Arguments("HIGH", "High")]
  [Arguments("critical", "Critical")]
  public async Task TryParseThreshold_AcceptsEachSeverityWordAsync(string value, string expected) {
    var parsed = AdvisorySeverities.TryParseThreshold(value, out var threshold);

    await Assert.That(parsed).IsTrue();
    await Assert.That(threshold).IsEqualTo(Enum.Parse<AdvisorySeverity>(expected));
  }

  [Test]
  public async Task TryParseThreshold_None_ParsesToNoThresholdAsync() {
    var parsed = AdvisorySeverities.TryParseThreshold("none", out var threshold);

    await Assert.That(parsed).IsTrue();
    await Assert.That(threshold).IsNull();
  }

  [Test]
  [Arguments("medium")]
  [Arguments("unknown")]
  [Arguments("")]
  public async Task TryParseThreshold_RejectsAnyOtherWordAsync(string value) {
    // "unknown" is a severity the report can show, but not a threshold a user can ask for.
    await Assert.That(AdvisorySeverities.TryParseThreshold(value, out _)).IsFalse();
  }

  [Test]
  [Arguments("Low", "Moderate", false)]
  [Arguments("Moderate", "Moderate", true)]
  [Arguments("Critical", "High", true)]
  [Arguments("High", "Critical", false)]
  public async Task Fails_IsAtOrAboveTheThresholdAsync(string severity, string threshold, bool expected) {
    await Assert.That(AdvisorySeverities.Fails(Enum.Parse<AdvisorySeverity>(severity), Enum.Parse<AdvisorySeverity>(threshold))).IsEqualTo(expected);
  }

  [Test]
  public async Task Fails_UnknownSeverity_FailsEvenAtCriticalAsync() {
    // An advisory that affects the exact resolved version but carries no severity is not
    // evidence of a harmless one. Failing closed keeps a missing field from passing a build.
    await Assert.That(AdvisorySeverities.Fails(AdvisorySeverity.Unknown, AdvisorySeverity.Critical)).IsTrue();
  }

  [Test]
  public async Task Fails_NoThreshold_NeverFailsAsync() {
    await Assert.That(AdvisorySeverities.Fails(AdvisorySeverity.Critical, null)).IsFalse();
    await Assert.That(AdvisorySeverities.Fails(AdvisorySeverity.Unknown, null)).IsFalse();
  }

  [Test]
  [Arguments("Low", "low")]
  [Arguments("Moderate", "moderate")]
  [Arguments("High", "high")]
  [Arguments("Critical", "critical")]
  [Arguments("Unknown", "unknown")]
  public async Task ToWord_IsTheLowerCaseNameAsync(string severity, string expected) {
    await Assert.That(AdvisorySeverities.ToWord(Enum.Parse<AdvisorySeverity>(severity))).IsEqualTo(expected);
  }
}
