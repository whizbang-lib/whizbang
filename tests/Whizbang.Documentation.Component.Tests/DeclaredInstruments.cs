// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.RegularExpressions;

namespace Whizbang.Documentation.Tests;

/// <summary>The kind of an instrument, as far as its exposed Prometheus name is concerned.</summary>
internal enum InstrumentKind {
  /// <summary>A gauge or up-down counter: exposed under its name alone.</summary>
  Gauge,

  /// <summary>A monotonic counter: exposed with <c>_total</c>.</summary>
  Counter,

  /// <summary>A histogram: exposed as <c>_bucket</c>, <c>_sum</c> and <c>_count</c>.</summary>
  Histogram,
}

/// <summary>One instrument a meter in the library declares.</summary>
/// <param name="Name">The instrument's OpenTelemetry name, for example <c>whizbang.table.estimated_bytes</c>.</param>
/// <param name="Unit">Its unit as declared, or null where it declares none.</param>
/// <param name="Kind">What it is exposed as.</param>
internal sealed partial record DeclaredInstrument(string Name, string? Unit, InstrumentKind Kind) {
  [GeneratedRegex(@"\{[^}]*\}")]
  private static partial Regex _annotation();

  /// <summary>
  /// The names a Prometheus exporter exposes this instrument under, following the OpenTelemetry Prometheus naming:
  /// dots become underscores, the unit is appended unless the name already ends with it, a counter gains
  /// <c>_total</c>, and a histogram is three series.
  /// </summary>
  public IEnumerable<string> PrometheusNames() {
    var name = _sanitize(Name);
    var unit = PrometheusUnit(Unit);
    if (unit.Length > 0 && !name.EndsWith(unit, StringComparison.Ordinal)) {
      name += "_" + unit;
    }
    return Kind switch {
      InstrumentKind.Counter => [name.EndsWith("_total", StringComparison.Ordinal) ? name : name + "_total"],
      InstrumentKind.Histogram => [name + "_bucket", name + "_sum", name + "_count"],
      _ => [name],
    };
  }

  /// <summary>The unit as the exporter spells it: annotations in braces dropped, abbreviations spelled out.</summary>
  internal static string PrometheusUnit(string? unit) {
    if (string.IsNullOrEmpty(unit)) {
      return "";
    }
    var plain = _annotation().Replace(unit, "");
    var slash = plain.IndexOf('/', StringComparison.Ordinal);
    if (slash >= 0) {
      var per = _perUnit(plain[(slash + 1)..]);
      var head = _unit(plain[..slash]);
      return head.Length == 0 ? "per_" + per : head + "_per_" + per;
    }
    return _unit(plain);
  }

  private static string _unit(string unit) => unit switch {
    "d" => "days",
    "h" => "hours",
    "min" => "minutes",
    "s" => "seconds",
    "ms" => "milliseconds",
    "us" => "microseconds",
    "ns" => "nanoseconds",
    "By" => "bytes",
    "%" => "percent",
    "1" => "",
    _ => _sanitize(unit),
  };

  private static string _perUnit(string unit) => unit switch {
    "s" => "second",
    "m" => "minute",
    "h" => "hour",
    "d" => "day",
    _ => _sanitize(unit),
  };

  private static string _sanitize(string text) {
    var sanitized = new StringBuilder(text.Length);
    foreach (var c in text) {
      sanitized.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
    }
    return sanitized.ToString();
  }
}

/// <summary>
/// Reads every instrument the library's meters declare out of the source under <c>src/</c>, so a check against it
/// follows the meters rather than a list kept by hand.
/// </summary>
/// <remarks>
/// <para>
/// An instrument is declared by a call to one of the meter's factory methods (<c>CreateCounter</c>,
/// <c>CreateHistogram</c>, <c>CreateObservableGauge</c> and the rest, and the library's own
/// <c>CreatePassiveCounter</c>). Its name is the call's first argument, a string literal or a <c>const</c> declared in
/// the same file or anywhere in the source; its unit is the <c>unit:</c> argument, or the positional one after the name
/// (after the callback, for an observable instrument).
/// </para>
/// <para>
/// A name that is neither a literal nor a constant (a parameter, as inside <c>PassiveCounter</c> itself) is not an
/// instrument declaration but a forwarder, and is skipped.
/// </para>
/// </remarks>
internal static partial class DeclaredInstruments {
  private static readonly Dictionary<string, InstrumentKind> _kinds = new(StringComparer.Ordinal) {
    ["CreateCounter"] = InstrumentKind.Counter,
    ["CreateObservableCounter"] = InstrumentKind.Counter,
    ["CreatePassiveCounter"] = InstrumentKind.Counter,
    ["CreateHistogram"] = InstrumentKind.Histogram,
    ["CreateGauge"] = InstrumentKind.Gauge,
    ["CreateUpDownCounter"] = InstrumentKind.Gauge,
    ["CreateObservableGauge"] = InstrumentKind.Gauge,
    ["CreateObservableUpDownCounter"] = InstrumentKind.Gauge,
    ["CreatePassiveUpDownCounter"] = InstrumentKind.Gauge,
  };

  [GeneratedRegex(@"\.(?<method>Create(?:Counter|Histogram|Gauge|UpDownCounter|ObservableCounter|ObservableGauge|ObservableUpDownCounter|PassiveCounter|PassiveUpDownCounter))\s*(?:<[^<>()]*>)?\s*\(")]
  private static partial Regex _factoryCall();

  [GeneratedRegex(@"^\s*[A-Za-z_][A-Za-z0-9_]*\s*:(?!:)")]
  private static partial Regex _label();

  [GeneratedRegex(@"const\s+string\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*""(?<value>[^""]*)""\s*;")]
  private static partial Regex _constant();

  /// <summary>Every instrument declared in the C# source under <paramref name="sourceRoot"/>.</summary>
  public static IReadOnlyList<DeclaredInstrument> Read(string sourceRoot) {
    var files = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
      .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                  && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
      .Select(path => File.ReadAllText(path))
      .ToList();
    var globalConstants = _constants(files.SelectMany(text => _constant().Matches(text)));
    return [.. files.SelectMany(text => Parse(text, globalConstants)).Distinct()];
  }

  /// <summary>Every instrument declared in <paramref name="source"/>.</summary>
  /// <param name="source">One C# file's text.</param>
  /// <param name="globalConstants">String constants declared anywhere, for a name or unit declared in another file.</param>
  public static IEnumerable<DeclaredInstrument> Parse(string source, IReadOnlyDictionary<string, string?> globalConstants) {
    var local = _constants(_constant().Matches(source));
    string? resolve(string expression) {
      var text = expression.Trim();
      if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') {
        return text[1..^1];
      }
      var identifier = text[(text.LastIndexOf('.') + 1)..];
      if (local.TryGetValue(identifier, out var value)) {
        return value;
      }
      return globalConstants.GetValueOrDefault(identifier);
    }

    foreach (Match call in _factoryCall().Matches(source)) {
      var method = call.Groups["method"].Value;
      var arguments = _arguments(source, call.Index + call.Length);
      if (arguments.Count == 0) {
        continue;
      }
      var name = resolve(_unnamed(arguments[0]));
      if (name is null) {
        continue;
      }
      yield return new DeclaredInstrument(name, _unit(arguments, method, resolve), _kinds[method]);
    }
  }

  private static string? _unit(List<string> arguments, string method, Func<string, string?> resolve) {
    var named = arguments.FirstOrDefault(a => a.TrimStart().StartsWith("unit:", StringComparison.Ordinal));
    if (named is not null) {
      return resolve(named.TrimStart()["unit:".Length..]);
    }
    var position = method.StartsWith("CreateObservable", StringComparison.Ordinal) ? 2 : 1;
    if (arguments.Count <= position || arguments[position].Contains(':', StringComparison.Ordinal)) {
      return null;
    }
    return resolve(arguments[position]);
  }

  /// <summary>An argument without its <c>name:</c> label.</summary>
  private static string _unnamed(string argument) {
    var match = _label().Match(argument);
    return match.Success ? argument[match.Length..] : argument;
  }

  /// <summary>The top-level arguments of the call whose argument list starts at <paramref name="start"/>.</summary>
  private static List<string> _arguments(string source, int start) {
    var arguments = new List<string>();
    var depth = 0;
    var current = new StringBuilder();
    var i = start;
    while (i < source.Length) {
      var c = source[i];
      if (c == '"') {
        var end = _endOfString(source, i);
        current.Append(source, i, end - i + 1);
        i = end + 1;
        continue;
      }
      if (c is '(' or '[' or '{') {
        depth++;
      } else if (c is ')' or ']' or '}') {
        if (depth == 0) {
          arguments.Add(current.ToString());
          return [.. arguments.Where(a => a.Trim().Length > 0)];
        }
        depth--;
      } else if (c == ',' && depth == 0) {
        arguments.Add(current.ToString());
        current.Clear();
        i++;
        continue;
      }
      current.Append(c);
      i++;
    }
    return [];
  }

  private static int _endOfString(string source, int quote) {
    var i = quote + 1;
    while (i < source.Length) {
      if (source[i] == '"') {
        return i;
      }
      // A backslash escapes the character after it.
      i += source[i] == '\\' ? 2 : 1;
    }
    return source.Length - 1;
  }

  private static Dictionary<string, string?> _constants(IEnumerable<Match> matches) {
    var constants = new Dictionary<string, string?>(StringComparer.Ordinal);
    foreach (var match in matches) {
      var name = match.Groups["name"].Value;
      var value = match.Groups["value"].Value;
      // A name declared twice with different values is ambiguous, so it resolves to nothing rather than to either.
      constants[name] = constants.TryGetValue(name, out var existing) && existing != value ? null : value;
    }
    return constants;
  }
}
