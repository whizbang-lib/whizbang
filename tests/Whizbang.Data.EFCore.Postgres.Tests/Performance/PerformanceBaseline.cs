using System.Globalization;
using System.Text;

namespace Whizbang.Data.EFCore.Postgres.Tests.Performance;

/// <summary>
/// The committed record of what each measured scenario costs, and the report of a run against it.
/// </summary>
/// <remarks>
/// <para>
/// A measuring instrument first and a gate second. Every measure is recorded and every drift is
/// shown, but a run fails only on a ceiling the baseline file declares and justifies, so a three
/// percent wobble between two machines does not cry wolf while a forty-fold regression cannot be
/// missed. A measure with no ceiling is reported and never fails.
/// </para>
/// <para>
/// The file is plain text on purpose: one measure per line, tab separated, sorted by name. A
/// performance baseline is only useful if a reviewer can read the diff, and a JSON document
/// re-serialized by a tool produces a diff nobody reads.
/// </para>
/// </remarks>
public sealed class PerformanceBaseline {
  private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

  /// <summary>A recorded measure: its value, the unit it is in, and the ceiling it may not pass.</summary>
  public readonly record struct Entry(double Value, string Unit, double? Ceiling, string Note);

  /// <summary>The file the baseline lives in, beside the scenarios that write it.</summary>
  public static string DefaultPath =>
    Path.Combine(AppContext.BaseDirectory, "Performance", "baseline.tsv");

  public static PerformanceBaseline Load(string path) {
    var baseline = new PerformanceBaseline();
    if (!File.Exists(path)) {
      return baseline;
    }
    foreach (var line in File.ReadAllLines(path)) {
      if (line.Length == 0 || line[0] == '#') {
        continue;
      }
      var parts = line.Split('\t');
      if (parts.Length < 3) {
        continue;
      }
      var ceiling = parts.Length > 3 && parts[3].Length > 0
        ? double.Parse(parts[3], CultureInfo.InvariantCulture)
        : (double?)null;
      var note = parts.Length > 4 ? parts[4] : "";
      baseline._entries[parts[0]] = new Entry(
        double.Parse(parts[1], CultureInfo.InvariantCulture), parts[2], ceiling, note);
    }
    return baseline;
  }

  public bool TryGet(string name, out Entry entry) => _entries.TryGetValue(name, out entry);

  /// <summary>
  /// A run's measures against the baseline, as a table a person can read, plus the verdicts a gate
  /// acts on.
  /// </summary>
  public sealed class Report(PerformanceBaseline baseline, string scenario) {
    private readonly List<(string Name, double Value, string Unit, Entry? Baseline)> _rows = [];
    private readonly PerformanceBaseline _baseline = baseline;
    private readonly string _scenario = scenario;

    /// <summary>Records a measure, normalized to a unit of work.</summary>
    public Report Measure(string name, double value, string unit) {
      _rows.Add((name, value, unit, _baseline.TryGet(name, out var e) ? e : null));
      return this;
    }

    /// <summary>The measures that passed a ceiling the baseline declares.</summary>
    public IReadOnlyList<string> Breaches => [.. _rows
      .Where(r => r.Baseline is { Ceiling: not null } b && r.Value > b.Ceiling!.Value)
      .Select(r => $"{r.Name} = {r.Value.ToString("N1", CultureInfo.InvariantCulture)} {r.Unit}, "
        + $"ceiling {r.Baseline!.Value.Ceiling!.Value.ToString("N1", CultureInfo.InvariantCulture)}"
        + (r.Baseline!.Value.Note.Length > 0 ? $" ({r.Baseline!.Value.Note})" : ""))];

    /// <summary>The report, as a person reads it: the measure, the baseline, and the drift.</summary>
    public string Render() {
      var sb = new StringBuilder();
      sb.Append("\n=== ").Append(_scenario).Append(" ===\n");
      sb.Append(string.Format(CultureInfo.InvariantCulture,
        "{0,-64}{1,14}{2,14}{3,11}  {4}\n", "measure", "this run", "baseline", "drift", "unit"));
      foreach (var (name, value, unit, recorded) in _rows) {
        var thisRun = value.ToString("N1", CultureInfo.InvariantCulture);
        var was = recorded is { } b ? b.Value.ToString("N1", CultureInfo.InvariantCulture) : "-";
        var drift = "-";
        if (recorded is { } bb && bb.Value > 0) {
          drift = ((value - bb.Value) / bb.Value * 100).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
        } else if (recorded is { } zb && zb.Value == 0 && value > 0) {
          drift = "new cost";
        }
        var ceiling = recorded is { Ceiling: not null } cb
          ? $"  (ceiling {cb.Ceiling!.Value.ToString("N0", CultureInfo.InvariantCulture)})"
          : "";
        sb.Append(string.Format(CultureInfo.InvariantCulture,
          "{0,-64}{1,14}{2,14}{3,11}  {4}{5}\n", name, thisRun, was, drift, unit, ceiling));
      }
      return sb.ToString();
    }

    /// <summary>
    /// The lines this run would contribute to the baseline file, so a person can paste an
    /// intentional change in rather than hand-editing numbers.
    /// </summary>
    public string RenderBaselineLines() => string.Concat(_rows
      .OrderBy(r => r.Name, StringComparer.Ordinal)
      .Select(r => string.Format(CultureInfo.InvariantCulture, "{0}\t{1:0.###}\t{2}\t{3}\t{4}\n",
        r.Name, r.Value, r.Unit,
        r.Baseline is { Ceiling: not null } b ? b.Ceiling!.Value.ToString("0.###", CultureInfo.InvariantCulture) : "",
        r.Baseline?.Note ?? "")));
  }
}
