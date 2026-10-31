// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Whizbang.Documentation.Tests;

/// <summary>
/// Keeps <c>dashboards/grafana/whizbang-storage.json</c> in step with the meters it reads: a renamed or removed
/// instrument fails the build here, rather than leaving a panel that silently shows nothing.
/// </summary>
/// <remarks>
/// <para>
/// The names the dashboard may use are derived from the meter definitions in <c>src/</c> (see
/// <see cref="DeclaredInstruments"/>), turned into the names a Prometheus exporter exposes them under, and every
/// <c>whizbang_</c> metric a panel or variable query names must be one of them.
/// </para>
/// <para>
/// Component, not unit: the test reads the repository's source and the dashboard from the file system.
/// </para>
/// </remarks>
/// <docs>operations/observability/storage-dashboard#drift-test</docs>
/// <code-under-test>dashboards/grafana/whizbang-storage.json</code-under-test>
[Category("Documentation")]
public partial class StorageDashboardTests {
  [GeneratedRegex(@"\bwhizbang_[a-z0-9_]+")]
  private static partial Regex _metricName();

  [GeneratedRegex(@"\b(job|database)\s*(=|!=|=~|!~)\s*""(?!\$)")]
  private static partial Regex _literalSelector();

  private static readonly Lazy<IReadOnlyList<DeclaredInstrument>> _declared =
    new(() => DeclaredInstruments.Read(Path.Combine(_repositoryRoot(), "src")));

  private static readonly Lazy<HashSet<string>> _exposed =
    new(() => [.. _declared.Value.SelectMany(i => i.PrometheusNames())]);

  [Test]
  public async Task EveryMetricTheDashboardNames_IsDeclaredByAMeterInTheLibraryAsync() {
    var named = _metricsNamed(await File.ReadAllTextAsync(_dashboardPath()));

    await Assert.That(named.Count).IsGreaterThan(10)
      .Because("a dashboard whose queries could not be read would pass vacuously");
    await Assert.That(named.Where(name => !_exposed.Value.Contains(name)).Order(StringComparer.Ordinal).ToList()).IsEmpty()
      .Because("every metric a panel reads must be one a meter in the library declares, under the name Prometheus exposes it by");
  }

  [Test]
  public async Task AMetricNoMeterDeclares_IsReportedAsync() {
    // The control: the same check, against a panel naming a misspelled table gauge and a real one.
    const string dashboard = """
      {"panels": [{"type": "timeseries", "targets": [
        {"expr": "sum(whizbang_table_estimated_bites{job=~\"$job\"})"},
        {"expr": "sum(whizbang_table_estimated_bytes{job=~\"$job\"})"}]}]}
      """;

    var unknown = _metricsNamed(dashboard).Where(name => !_exposed.Value.Contains(name)).ToList();

    await Assert.That(unknown).IsEquivalentTo((List<string>)["whizbang_table_estimated_bites"])
      .Because("the check must fail for a name no meter declares and pass for one a meter does");
  }

  [Test]
  public async Task TheExposedName_FollowsTheUnitAndTheKind_SoAWrongSuffixIsReportedAsync() {
    string[] wrong = [
      // The sampled gauge's unit is bytes and its name does not end with it, so Prometheus appends it.
      "whizbang_event_store_sampled_bytes_per_event",
      // A gauge is never _total, and a counter is never bare.
      "whizbang_table_estimated_bytes_total",
      "whizbang_maintenance_rows_affected",
      // A histogram in milliseconds is exposed with its unit spelled out, as buckets.
      "whizbang_maintenance_task_duration_bucket",
    ];
    string[] right = [
      "whizbang_event_store_sampled_bytes_per_event_bytes",
      "whizbang_table_estimated_bytes",
      "whizbang_maintenance_rows_affected_total",
      "whizbang_maintenance_task_duration_milliseconds_bucket",
      "whizbang_queue_estimated_depth_messages",
      "whizbang_capability_state",
    ];

    await Assert.That(wrong.Where(_exposed.Value.Contains).ToList()).IsEmpty()
      .Because("a query that names the instrument but not its exposed series reads nothing in Prometheus");
    await Assert.That(right.Where(name => !_exposed.Value.Contains(name)).ToList()).IsEmpty()
      .Because("the derived names are the ones the exporter writes");
  }

  [Test]
  public async Task TheDeclaredInstruments_AreReadFromTheMeterDefinitionsAsync() {
    await Assert.That(_declared.Value).Contains(new DeclaredInstrument("whizbang.table.component_bytes", "bytes", InstrumentKind.Gauge))
      .Because("a unit given by a constant (BYTES_UNIT) is resolved");
    await Assert.That(_declared.Value).Contains(new DeclaredInstrument("whizbang.maintenance.task_duration", "ms", InstrumentKind.Histogram))
      .Because("a named unit argument is read");
    await Assert.That(_declared.Value).Contains(new DeclaredInstrument("whizbang.maintenance.rows_affected", null, InstrumentKind.Counter))
      .Because("the library's passive counter is a counter");
    await Assert.That(_declared.Value).Contains(new DeclaredInstrument("whizbang.capability.state", null, InstrumentKind.Gauge));
  }

  [Test]
  public async Task Parse_ReadsPositionalUnits_ConstantNames_AndSkipsForwardersAsync() {
    const string source = """
      private const string ROWS = "whizbang.sample.rows";
      void Declare(Meter meter, string name) {
        meter.CreateHistogram<double>("whizbang.sample.latency", "ms", "Latency");
        meter.CreateObservableGauge(ROWS, () => _rows, "rows", "Rows");
        meter.CreateCounter<long>(name: "whizbang.sample.seen", description: "Seen: every one");
        meter.CreateObservableCounter(name, _observe, unit, description);
      }
      """;

    var parsed = DeclaredInstruments.Parse(source, new Dictionary<string, string?>()).ToList();

    await Assert.That(parsed).IsEquivalentTo((List<DeclaredInstrument>)[
      new("whizbang.sample.latency", "ms", InstrumentKind.Histogram),
      new("whizbang.sample.rows", "rows", InstrumentKind.Gauge),
      new("whizbang.sample.seen", null, InstrumentKind.Counter),
    ]).Because("a parameter name is a forwarder, not a declaration, and a named description is not a unit");
  }

  [Test]
  [Arguments("{item}/s", "per_second")]
  [Arguments("{message}", "")]
  [Arguments("By", "bytes")]
  [Arguments("s", "seconds")]
  [Arguments("1", "")]
  [Arguments("doorbells", "doorbells")]
  [Arguments(null, "")]
  public async Task PrometheusUnit_SpellsTheUnitTheWayTheExporterDoesAsync(string? unit, string expected) {
    await Assert.That(DeclaredInstrument.PrometheusUnit(unit)).IsEqualTo(expected);
  }

  [Test]
  public async Task TheDashboard_NamesNoEnvironment_ItsDataSourceAndScopeAreVariablesAsync() {
    var text = await File.ReadAllTextAsync(_dashboardPath());
    using var dashboard = JsonDocument.Parse(text);
    var variables = dashboard.RootElement.GetProperty("templating").GetProperty("list").EnumerateArray()
      .Select(v => v.GetProperty("name").GetString()).ToList();
    var dataSources = _dataSourceUids(dashboard.RootElement).Distinct().ToList();

    await Assert.That(variables).Contains("datasource");
    await Assert.That(variables).Contains("job");
    await Assert.That(variables).Contains("database");
    await Assert.That(dataSources).IsEquivalentTo((List<string?>)["${datasource}"])
      .Because("a data source named in the file is one environment's; every panel and query takes the variable");
    await Assert.That(_literalSelector().IsMatch(text)).IsFalse()
      .Because("a service or database named in a query is one deployment's; queries select them through $job and $database");
  }

  [Test]
  public async Task TheLiteralSelectorCheck_SeesANamedDatabaseAsync() {
    // The control for the check above: a query that names its database is caught, and one that takes the variable is not.
    await Assert.That(_literalSelector().IsMatch("""whizbang_table_estimated_bytes{database="orders"}""")).IsTrue();
    await Assert.That(_literalSelector().IsMatch("""whizbang_table_estimated_bytes{database=~"$database"}""")).IsFalse();
  }

  [Test]
  public async Task EveryTableStatisticsQuery_TakesTheMaxAcrossInstancesAsync() {
    var exprs = _panelExpressions(await File.ReadAllTextAsync(_dashboardPath()));

    var reporting = exprs.Where(_readsAPerInstanceCopy).ToList();
    await Assert.That(reporting.Count).IsGreaterThan(10);
    await Assert.That(reporting.Where(expr => !_takesTheMax(expr)).ToList()).IsEmpty()
      .Because("every instance reports the same catalog reading and the sampler's one instance is a copy too, so a plain sum "
             + "would count each table once per instance");
  }

  [Test]
  public async Task TheMaxCheck_SeesAPlainSumAsync() {
    // The control for the check above.
    await Assert.That(_takesTheMax("sum(whizbang_table_estimated_bytes{job=~\"$job\"})")).IsFalse();
    await Assert.That(_takesTheMax("sum(max by (database, table_name) (whizbang_table_estimated_bytes{job=~\"$job\"}))")).IsTrue();
  }

  [Test]
  public async Task TheRewriteThreshold_DefaultsToTheDatabasesDefaultOfThreeAsync() {
    using var dashboard = JsonDocument.Parse(await File.ReadAllTextAsync(_dashboardPath()));
    var threshold = dashboard.RootElement.GetProperty("templating").GetProperty("list").EnumerateArray()
      .Single(v => v.GetProperty("name").GetString() == "rewrite_threshold");

    await Assert.That(threshold.GetProperty("query").GetString()).IsEqualTo("3")
      .Because("the line on the bloat panel is where the maintenance cycle queues a rewrite, table_rewrite_bloat_threshold, whose default is 3.0");
    await Assert.That(_panelExpressions(dashboard.RootElement.GetRawText())).Contains("vector($rewrite_threshold)");
  }

  private static bool _readsAPerInstanceCopy(string expr) =>
    expr.Contains("whizbang_table_", StringComparison.Ordinal)
    || expr.Contains("whizbang_queue_", StringComparison.Ordinal)
    || expr.Contains("whizbang_event_store_", StringComparison.Ordinal);

  /// <summary>Whether every per-instance metric in <paramref name="expr"/> is inside a <c>max by</c>.</summary>
  private static bool _takesTheMax(string expr) {
    foreach (Match metric in _metricName().Matches(expr)) {
      if (!_readsAPerInstanceCopy(metric.Value)) {
        continue;
      }
      var before = expr[..metric.Index];
      var opened = before.LastIndexOf("max by (", StringComparison.Ordinal);
      if (opened < 0 || _depth(before[opened..]) <= 0) {
        return false;
      }
    }
    return true;
  }

  /// <summary>How many parentheses are open at the end of <paramref name="text"/>, past the grouping list's own.</summary>
  private static int _depth(string text) {
    var depth = 0;
    foreach (var c in text) {
      if (c == '(') {
        depth++;
      } else if (c == ')') {
        depth--;
      }
    }
    return depth;
  }

  private static List<string> _panelExpressions(string dashboardJson) {
    using var dashboard = JsonDocument.Parse(dashboardJson);
    var exprs = new List<string>();
    _collect(dashboard.RootElement, exprs);
    return exprs;

    static void _collect(JsonElement element, List<string> exprs) {
      if (element.ValueKind == JsonValueKind.Object) {
        foreach (var property in element.EnumerateObject()) {
          if (property.Name == "expr" && property.Value.ValueKind == JsonValueKind.String) {
            exprs.Add(property.Value.GetString()!);
          }
          _collect(property.Value, exprs);
        }
      } else if (element.ValueKind == JsonValueKind.Array) {
        foreach (var item in element.EnumerateArray()) {
          _collect(item, exprs);
        }
      }
    }
  }

  /// <summary>Every <c>whizbang_</c> metric the panel queries and the variable queries of a dashboard name.</summary>
  private static HashSet<string> _metricsNamed(string dashboardJson) {
    using var dashboard = JsonDocument.Parse(dashboardJson);
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var query in _queries(dashboard.RootElement)) {
      foreach (Match match in _metricName().Matches(query)) {
        names.Add(match.Value);
      }
    }
    return names;
  }

  /// <summary>Every <c>expr</c>, and every variable <c>query</c> or <c>definition</c>, anywhere in the document.</summary>
  private static IEnumerable<string> _queries(JsonElement element) {
    if (element.ValueKind == JsonValueKind.Object) {
      foreach (var property in element.EnumerateObject()) {
        if (property.Value.ValueKind == JsonValueKind.String && property.Name is "expr" or "query" or "definition") {
          yield return property.Value.GetString()!;
        }
        foreach (var nested in _queries(property.Value)) {
          yield return nested;
        }
      }
    } else if (element.ValueKind == JsonValueKind.Array) {
      foreach (var item in element.EnumerateArray()) {
        foreach (var nested in _queries(item)) {
          yield return nested;
        }
      }
    }
  }

  /// <summary>The <c>uid</c> of every <c>datasource</c> object in the document.</summary>
  private static IEnumerable<string?> _dataSourceUids(JsonElement element) {
    if (element.ValueKind == JsonValueKind.Object) {
      foreach (var property in element.EnumerateObject()) {
        if (property.Name == "datasource" && property.Value.ValueKind == JsonValueKind.Object) {
          yield return property.Value.TryGetProperty("uid", out var uid) ? uid.GetString() : null;
        }
        foreach (var nested in _dataSourceUids(property.Value)) {
          yield return nested;
        }
      }
    } else if (element.ValueKind == JsonValueKind.Array) {
      foreach (var item in element.EnumerateArray()) {
        foreach (var nested in _dataSourceUids(item)) {
          yield return nested;
        }
      }
    }
  }

  private static string _dashboardPath() => Path.Combine(_repositoryRoot(), "dashboards", "grafana", "whizbang-storage.json");

  private static string _repositoryRoot() {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whizbang.slnx"))) {
      dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("Whizbang.slnx not found above the test binary.");
  }
}
