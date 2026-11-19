// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Documentation.Tests;

/// <summary>
/// #2365: keeps <c>dashboards/grafana/whizbang-integrity.json</c> and <c>dashboards/prometheus/whizbang-integrity-alerts.yml</c>
/// in step with the meters they read: a renamed or removed instrument fails the build here, rather than leaving a panel
/// that shows nothing or an alert that can never fire. The thresholds that mirror a library default are locked to it.
/// </summary>
/// <remarks>
/// Component, not unit: the test reads the repository's source, the dashboard and the rules from the file system. The
/// declared names come from <see cref="DeclaredInstruments"/>, as the storage dashboard's do.
/// </remarks>
/// <docs>resilience/stream-integrity#integrity-dashboard-and-alerts</docs>
/// <code-under-test>dashboards/grafana/whizbang-integrity.json</code-under-test>
/// <code-under-test>dashboards/prometheus/whizbang-integrity-alerts.yml</code-under-test>
[Category("Documentation")]
public partial class IntegrityDashboardTests {
  [GeneratedRegex(@"\bwhizbang_[a-z0-9_]+")]
  private static partial Regex _metricName();

  [GeneratedRegex(@"- alert: (\w+)")]
  private static partial Regex _alertName();

  [GeneratedRegex(@"\b(job|peer|origin_service_id)\s*(=|!=|=~|!~)\s*""(?!\$)")]
  private static partial Regex _literalSelector();

  private static readonly Lazy<HashSet<string>> _exposed = new(() =>
    [.. DeclaredInstruments.Read(Path.Combine(_repositoryRoot(), "src")).SelectMany(i => i.PrometheusNames())]);

  [Test]
  public async Task EveryMetricTheDashboardNames_IsDeclaredByAMeterInTheLibraryAsync() {
    var named = _metricsIn(_queries(await File.ReadAllTextAsync(_dashboardPath())));

    await Assert.That(named.Count).IsGreaterThan(15).Because("a dashboard whose queries could not be read would pass vacuously");
    await Assert.That(named.Where(n => !_exposed.Value.Contains(n)).Order(StringComparer.Ordinal).ToList()).IsEmpty()
      .Because("every metric a panel reads must be one a meter declares, under the name Prometheus exposes it by");
  }

  [Test]
  public async Task EveryMetricTheAlertRulesName_IsDeclaredByAMeterInTheLibraryAsync() {
    var named = _metricsIn(_ruleLines(await File.ReadAllTextAsync(_rulesPath())));

    await Assert.That(named.Count).IsGreaterThan(7).Because("rules whose expressions could not be read would pass vacuously");
    await Assert.That(named.Where(n => !_exposed.Value.Contains(n)).Order(StringComparer.Ordinal).ToList()).IsEmpty()
      .Because("an alert on a metric no meter declares can never fire");
  }

  [Test]
  public async Task AMisspelledMetric_IsReported_AndADeclaredOneIsNotAsync() {
    // The control for both checks above.
    var named = _metricsIn(_ruleLines("""
      - alert: X
        expr: max(whizbang_stream_integrity_pair_halted_secs) > 1 or max(whizbang_stream_integrity_pair_halted_seconds) > 1
      """));

    await Assert.That(named.Where(n => !_exposed.Value.Contains(n)).ToList())
      .IsEquivalentTo((List<string>)["whizbang_stream_integrity_pair_halted_secs"]);
  }

  [Test]
  public async Task TheRules_CoverEveryIntegrityCondition_TheIssueAsksForAsync() {
    var alerts = _alertName().Matches(await File.ReadAllTextAsync(_rulesPath())).Select(m => m.Groups[1].Value).ToList();

    string[] required = [
      "WhizbangIntegrityPairHalted", "WhizbangIntegritySustainedDeficit", "WhizbangIntegrityRecheckDrift",
      "WhizbangIntegritySealNotAdvancing", "WhizbangIntegrityRecheckBehind", "WhizbangOperationalRetentionBehind",
      "WhizbangArchiveOrphanSweepRefused",
    ];
    await Assert.That(required.Where(r => !alerts.Contains(r)).ToList()).IsEmpty();
    await Assert.That(alerts.Count).IsEqualTo(alerts.Distinct().Count()).Because("two rules with one name shadow each other");
  }

  /// <summary>
  /// The halted-pair alert fires on the pair guard's own alert counter (owner decision, #2365), so it follows
  /// <c>PairGuard:HaltedAlertAfter</c> as configured: the rule writes no duration of its own.
  /// </summary>
  [Test]
  public async Task TheHaltedPairAlert_FiresOnTheGuardsOwnAlertCounter_WithNoThresholdOfItsOwnAsync() {
    var expr = _expression(await File.ReadAllTextAsync(_rulesPath()), "WhizbangIntegrityPairHalted");

    await Assert.That(expr).Contains("whizbang_stream_integrity_pair_halt_alerts_total")
      .Because("the guard counts a halt once it passes HaltedAlertAfter, whatever the service configured");
    await Assert.That(expr).DoesNotContain("pair_halted_seconds")
      .Because("a duration written into the rule drifts from the service's setting");
  }

  /// <summary>The retention alert fires at twice the default resting period, as the oldest-age gauge's description says.</summary>
  [Test]
  public async Task TheRetentionAlert_FiresAtTwiceTheDefaultRestingPeriodAsync() {
    var threshold = _threshold(await File.ReadAllTextAsync(_rulesPath()), "WhizbangOperationalRetentionBehind");

    await Assert.That(threshold).IsEqualTo(2 * new OperationalRetentionOptions().Default.TotalSeconds);
  }

  [Test]
  public async Task TheDashboard_NamesNoEnvironment_ItsDataSourceAndServiceAreVariablesAsync() {
    var text = await File.ReadAllTextAsync(_dashboardPath());
    using var dashboard = JsonDocument.Parse(text);
    var variables = dashboard.RootElement.GetProperty("templating").GetProperty("list").EnumerateArray()
      .Select(v => v.GetProperty("name").GetString()).ToList();

    await Assert.That(variables).Contains("datasource");
    await Assert.That(variables).Contains("job");
    await Assert.That(_literalSelector().IsMatch(text)).IsFalse()
      .Because("a service or lane named in a query is one deployment's; queries select them through variables");
    await Assert.That(_queries(text).Where(q => q.Contains("whizbang_", StringComparison.Ordinal)
      && !q.Contains("label_values", StringComparison.Ordinal) && !q.Contains("job=~\"$job\"", StringComparison.Ordinal)).ToList())
      .IsEmpty().Because("every panel follows the service selector");
  }

  private static double _threshold(string rules, string alert) {
    var expr = _expression(rules, alert);
    return double.Parse(expr[(expr.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture);
  }

  private static string _expression(string rules, string alert) {
    var at = rules.IndexOf($"- alert: {alert}", StringComparison.Ordinal);
    return rules[at..].Split('\n').First(l => l.TrimStart().StartsWith("expr:", StringComparison.Ordinal));
  }

  private static HashSet<string> _metricsIn(IEnumerable<string> texts) =>
    [.. texts.SelectMany(t => _metricName().Matches(t).Select(m => m.Value))];

  /// <summary>The rule file's lines, comments dropped: names in a comment are prose, not queries.</summary>
  private static IEnumerable<string> _ruleLines(string rules) =>
    rules.Split('\n').Where(l => !l.TrimStart().StartsWith('#'));

  /// <summary>Every <c>expr</c>, and every variable <c>query</c> or <c>definition</c>, anywhere in the dashboard.</summary>
  private static List<string> _queries(string dashboardJson) {
    using var dashboard = JsonDocument.Parse(dashboardJson);
    var found = new List<string>();
    _collect(dashboard.RootElement, found);
    return found;

    static void _collect(JsonElement element, List<string> found) {
      if (element.ValueKind == JsonValueKind.Object) {
        foreach (var property in element.EnumerateObject()) {
          if (property.Value.ValueKind == JsonValueKind.String && property.Name is "expr" or "query" or "definition") {
            found.Add(property.Value.GetString()!);
          }
          _collect(property.Value, found);
        }
      } else if (element.ValueKind == JsonValueKind.Array) {
        foreach (var item in element.EnumerateArray()) {
          _collect(item, found);
        }
      }
    }
  }

  private static string _dashboardPath() => Path.Combine(_repositoryRoot(), "dashboards", "grafana", "whizbang-integrity.json");

  private static string _rulesPath() => Path.Combine(_repositoryRoot(), "dashboards", "prometheus", "whizbang-integrity-alerts.yml");

  private static string _repositoryRoot() {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whizbang.slnx"))) {
      dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("Whizbang.slnx not found above the test binary.");
  }
}
