// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Collections.Frozen;
using BenchmarkDotNet.Attributes;
using Whizbang.Core.Messaging;

namespace Whizbang.Benchmarks;

/// <summary>
/// Whether the by-name type tables read on every message would gain from being frozen (#1756).
/// </summary>
/// <remarks>
/// <para>
/// The tables are <c>JsonContextRegistry</c>'s wire-name map (one entry per registered message type, read by
/// <c>GetTypeInfoByName</c> on every deserialization) and <c>ControlPlaneTypeRegistry</c>'s name set (about a dozen
/// entries, read on every received row). Both are <see cref="ConcurrentDictionary{TKey,TValue}"/> with ordinal string
/// keys today, because registration stays open for the life of the process: a module initializer runs when its
/// assembly first loads, which can be after the first message. A frozen table would have to be rebuilt on each late
/// registration, so its construction cost is measured alongside the lookups.
/// </para>
/// <para>
/// Every lookup first builds its key with <see cref="EventTypeMatchingHelper.NormalizeTypeName"/>, so the
/// normalization is measured too, for the short wire form ("Type, Assembly") and the fully assembly-qualified form.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[MarkdownExporter]
public class TypeNameLookupBenchmarks {
  [Params(16, 512)]
  public int Entries { get; set; }

  private string[] _keys = null!;
  private string[] _fullyQualifiedKeys = null!;
  private ConcurrentDictionary<string, object> _concurrent = null!;
  private Dictionary<string, object> _dictionary = null!;
  private FrozenDictionary<string, object> _frozen = null!;
  private FrozenDictionary<string, object>.AlternateLookup<ReadOnlySpan<char>> _frozenBySpan;
  private KeyValuePair<string, object>[] _pairs = null!;

  [GlobalSetup]
  public void Setup() {
    _keys = [.. Enumerable.Range(0, Entries).Select(i => $"Company.Contracts.Orders.Events.OrderEvent{i:D4}, Company.Contracts.Orders")];
    _fullyQualifiedKeys = [.. _keys.Select(k => k + ", Version=1.2.3.0, Culture=neutral, PublicKeyToken=null")];
    _pairs = [.. _keys.Select(k => new KeyValuePair<string, object>(k, new object()))];
    _concurrent = new ConcurrentDictionary<string, object>(_pairs, StringComparer.Ordinal);
    _dictionary = new Dictionary<string, object>(_pairs, StringComparer.Ordinal);
    _frozen = _pairs.ToFrozenDictionary(StringComparer.Ordinal);
    _frozenBySpan = _frozen.GetAlternateLookup<ReadOnlySpan<char>>();
  }

  // ---- Lookup alone, every key once per operation (the key is already built) ----

  [Benchmark(Baseline = true)]
  [BenchmarkCategory("Lookup")]
  public int Lookup_ConcurrentDictionary() {
    var hits = 0;
    foreach (var key in _keys) {
      hits += _concurrent.ContainsKey(key) ? 1 : 0;
    }
    return hits;
  }

  [Benchmark]
  [BenchmarkCategory("Lookup")]
  public int Lookup_Dictionary() {
    var hits = 0;
    foreach (var key in _keys) {
      hits += _dictionary.ContainsKey(key) ? 1 : 0;
    }
    return hits;
  }

  [Benchmark]
  [BenchmarkCategory("Lookup")]
  public int Lookup_FrozenDictionary() {
    var hits = 0;
    foreach (var key in _keys) {
      hits += _frozen.ContainsKey(key) ? 1 : 0;
    }
    return hits;
  }

  // ---- What one rebuild of a frozen table costs (paid on every late registration) ----

  [Benchmark]
  [BenchmarkCategory("Construct")]
  public FrozenDictionary<string, object> Construct_FrozenDictionary() => _pairs.ToFrozenDictionary(StringComparer.Ordinal);

  [Benchmark]
  [BenchmarkCategory("Construct")]
  public ConcurrentDictionary<string, object> Construct_ConcurrentDictionary() => new(_pairs, StringComparer.Ordinal);

  // ---- The whole by-name lookup as the registries do it: normalize the name, then look it up ----

  [Benchmark]
  [BenchmarkCategory("NormalizeAndLookup")]
  public int NormalizeThenLookup_WireForm_ConcurrentDictionary() {
    var hits = 0;
    foreach (var key in _keys) {
      hits += _concurrent.ContainsKey(EventTypeMatchingHelper.NormalizeTypeName(key)) ? 1 : 0;
    }
    return hits;
  }

  [Benchmark]
  [BenchmarkCategory("NormalizeAndLookup")]
  public int NormalizeThenLookup_WireForm_FrozenDictionary() {
    var hits = 0;
    foreach (var key in _keys) {
      hits += _frozen.ContainsKey(EventTypeMatchingHelper.NormalizeTypeName(key)) ? 1 : 0;
    }
    return hits;
  }

  [Benchmark]
  [BenchmarkCategory("NormalizeAndLookup")]
  public int NormalizeThenLookup_FullyQualified_ConcurrentDictionary() {
    var hits = 0;
    foreach (var key in _fullyQualifiedKeys) {
      hits += _concurrent.ContainsKey(EventTypeMatchingHelper.NormalizeTypeName(key)) ? 1 : 0;
    }
    return hits;
  }

  /// <summary>
  /// The span alternative for a non-generic fully qualified name: the normalized key is the prefix before
  /// ", Version=", so it can be looked up as a slice without building a string.
  /// </summary>
  [Benchmark]
  [BenchmarkCategory("NormalizeAndLookup")]
  public int SliceThenLookup_FullyQualified_FrozenAlternateLookup() {
    var hits = 0;
    foreach (var key in _fullyQualifiedKeys) {
      var span = key.AsSpan();
      var cut = span.IndexOf(", Version=", StringComparison.Ordinal);
      var normalized = cut < 0 ? span : span[..cut];
      if (_frozenBySpan.ContainsKey(normalized)) {
        hits++;
      }
    }
    return hits;
  }
}
