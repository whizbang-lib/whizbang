// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.BuildCost.Benchmarks;

/// <summary>
/// Ordinary application code that no Whizbang generator has any interest in: classes, records, structs,
/// interfaces and enums with fields, properties, constructors and methods full of ordinary calls, lambdas and
/// generic types, and no base list, attribute, partial modifier or nested type anywhere. Most of a consumer's
/// code looks like this, so a syntax predicate that admits any of it sends a share of the whole codebase to its
/// semantic transform, and the cost grows with the size of the consumer rather than with its use of Whizbang.
/// </summary>
/// <docs>operations/performance/build-cost</docs>
public static class OrdinaryCodeCorpus {
  /// <summary>Builds <paramref name="files"/> files of ordinary code as (path, source) pairs.</summary>
  public static IReadOnlyList<(string Path, string Source)> Build(int files) {
    var result = new List<(string, string)>(files);
    for (var i = 0; i < files; i++) {
      result.Add(($"Ordinary/Ledger{i}.cs", File(i)));
    }
    return result;
  }

  /// <summary>One file of ordinary code: a class, a record, a struct, an interface and an enum.</summary>
  public static string File(int i) => $$"""
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    namespace Consumer.Ordinary{{i / 25}};

    public sealed class Ledger{{i}} {
      private readonly Dictionary<string, int> _totals = new();
      private readonly List<string> _lines = [];
      private int _count;

      public Ledger{{i}}(Dictionary<string, int> seed, IReadOnlyList<string> names) {
        foreach (var pair in seed) {
          _totals[pair.Key] = pair.Value;
        }
        _lines.AddRange(names.Select(n => n.Trim()));
      }

      public string Name { get; set; } = "ledger{{i}}";
      public int Count => _count;

      public int Add(string key, int amount) {
        _totals.TryGetValue(key, out var current);
        _totals[key] = current + amount;
        _lines.Add(key.ToUpperInvariant());
        _count++;
        return _totals.Count;
      }

      public string Render() {
        var sb = new StringBuilder();
        foreach (var line in _lines.Where(l => l.Length > 0).OrderBy(l => l)) {
          sb.Append(line.Trim()).Append(',');
        }
        return sb.ToString().TrimEnd(',');
      }

      public async Task<int> SumAsync(IEnumerable<int> values) {
        await Task.Yield();
        return values.Select(v => v * 2).Where(v => v > 0).Sum();
      }

      public static Tuple<string, int> Pair(KeyValuePair<string, int> pair) =>
        Tuple.Create(pair.Key.ToLowerInvariant(), Math.Max(pair.Value, 0));

      public string Describe(Func<string, int, string> format) =>
        string.Join(";", _totals.Select(kv => format(kv.Key, kv.Value)).ToList().AsReadOnly());
    }

    public sealed record Point{{i}}(int X, int Y) {
      public double Length => Math.Sqrt((X * X) + (Y * Y));
      public Point{{i}} Move(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
    }

    public readonly struct Span{{i}} {
      public Span{{i}}(int start, int length) {
        Start = start;
        Length = length;
      }

      public int Start { get; }
      public int Length { get; }
      public int End => Start + Length;
      public bool Contains(int value) => value >= Start && value < End;
    }

    public interface IFormatter{{i}} {
      string Format(Ledger{{i}} ledger);
      IReadOnlyDictionary<string, List<int>> Group(IEnumerable<KeyValuePair<string, int>> pairs);
    }

    public enum Mode{{i}} {
      None,
      Fast,
      Careful,
    }

    public static class LedgerExtensions{{i}} {
      public static string Summary(this Ledger{{i}} ledger, Mode{{i}} mode) {
        var text = ledger.Render();
        return mode switch {
          Mode{{i}}.Fast => text.Substring(0, Math.Min(text.Length, 10)),
          Mode{{i}}.Careful => text.Replace(",", ", ").Trim(),
          _ => string.Empty,
        };
      }
    }
    """;
}
