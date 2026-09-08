using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Numerics;

namespace Whizbang.Core.Observability;

/// <summary>
/// A counter the process accumulates and the meter observes. The worker that does the work adds
/// to a live count held in memory; nothing is pushed to the metrics pipeline. At every collection
/// the meter reads the counts it holds and reports them, one measurement per tag set, so the
/// series exist from the moment the counter is constructed (at zero) and a quiet subsystem reads
/// as zero rather than as a missing meter (issue #711).
/// </summary>
/// <remarks>
/// <para>
/// Registered on the meter as an observable counter (cumulative, monotonic) or an observable
/// up-down counter. Exporters compute rates and deltas from the cumulative values exactly as they
/// do for a pushed counter; dashboards see the same series names and tags.
/// </para>
/// <para>
/// The untagged series always exists. A closed tag domain (an enum, a fixed category set) is
/// declared with <see cref="Touch(KeyValuePair{string, object?})"/> so each of its series exists at
/// zero before the first real count; open tag values (type names, origins, stream ids) appear on
/// first use, which is correct: nothing is fabricated. Histograms are not counters and keep the
/// push model: a distribution has no meaningful value before its first sample.
/// </para>
/// </remarks>
/// <typeparam name="T">The count's numeric type.</typeparam>
/// <docs>operations/observability/metrics</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/PassiveCounterTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Observability/PassiveCounterDriftLockTests.cs</tests>
public sealed class PassiveCounter<T> where T : struct, INumberBase<T> {
  private readonly Cell _untagged = new();
  private readonly ConcurrentDictionary<TagSet, Cell> _series = new();
  private readonly Instrument _instrument;

  internal PassiveCounter(Meter meter, string name, string? unit, string? description, bool monotonic) {
    ArgumentNullException.ThrowIfNull(meter);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    _instrument = monotonic
      ? meter.CreateObservableCounter(name, _observe, unit, description)
      : meter.CreateObservableUpDownCounter(name, _observe, unit, description);
  }

  /// <summary>The instrument name, as the meter reports it.</summary>
  public string Name => _instrument.Name;

  /// <summary>The meter this counter reports on.</summary>
  public Meter Meter => _instrument.Meter;

  /// <summary>The observable instrument registered on the meter.</summary>
  public Instrument Instrument => _instrument;

  /// <summary>Adds to the untagged count.</summary>
  /// <param name="delta">The amount to add.</param>
  public void Add(T delta) => _untagged.Add(delta);

  /// <summary>Adds to the count for one tag.</summary>
  /// <param name="delta">The amount to add.</param>
  /// <param name="tag">The tag identifying the series.</param>
  public void Add(T delta, KeyValuePair<string, object?> tag) => _cell([tag]).Add(delta);

  /// <summary>Adds to the count for two tags.</summary>
  /// <param name="delta">The amount to add.</param>
  /// <param name="tag1">The first tag.</param>
  /// <param name="tag2">The second tag.</param>
  public void Add(T delta, KeyValuePair<string, object?> tag1, KeyValuePair<string, object?> tag2) =>
    _cell([tag1, tag2]).Add(delta);

  /// <summary>Adds to the count for three tags.</summary>
  /// <param name="delta">The amount to add.</param>
  /// <param name="tag1">The first tag.</param>
  /// <param name="tag2">The second tag.</param>
  /// <param name="tag3">The third tag.</param>
  public void Add(T delta, KeyValuePair<string, object?> tag1, KeyValuePair<string, object?> tag2, KeyValuePair<string, object?> tag3) =>
    _cell([tag1, tag2, tag3]).Add(delta);

  /// <summary>Adds to the count for a tag set.</summary>
  /// <param name="delta">The amount to add.</param>
  /// <param name="tags">The tags identifying the series.</param>
  public void Add(T delta, params ReadOnlySpan<KeyValuePair<string, object?>> tags) {
    if (tags.IsEmpty) {
      _untagged.Add(delta);
      return;
    }
    _cell(tags).Add(delta);
  }

  /// <summary>Adds to the count for a tag list.</summary>
  /// <param name="delta">The amount to add.</param>
  /// <param name="tags">The tags identifying the series.</param>
  public void Add(T delta, TagList tags) {
    if (tags.Count == 0) {
      _untagged.Add(delta);
      return;
    }
    var array = new KeyValuePair<string, object?>[tags.Count];
    tags.CopyTo(array);
    _cell(array).Add(delta);
  }

  /// <summary>Declares a series of a closed tag domain so it exists at zero before its first count.</summary>
  /// <param name="tag">The tag identifying the series.</param>
  public void Touch(KeyValuePair<string, object?> tag) => _ = _cell([tag]);

  /// <summary>Declares one series per known value of a closed tag domain.</summary>
  /// <param name="tagKey">The tag key.</param>
  /// <param name="tagValues">Every value the domain can take.</param>
  public void Touch(string tagKey, IEnumerable<string> tagValues) {
    ArgumentNullException.ThrowIfNull(tagValues);
    foreach (var value in tagValues) {
      _ = _cell([new KeyValuePair<string, object?>(tagKey, value)]);
    }
  }

  /// <summary>Declares a series of a multi-dimensional closed domain so it exists at zero.</summary>
  /// <param name="tags">The exact tags the recorder will later write.</param>
  public void Touch(TagList tags) {
    if (tags.Count == 0) {
      return;
    }
    var array = new KeyValuePair<string, object?>[tags.Count];
    tags.CopyTo(array);
    _ = _cell(array);
  }

  private Cell _cell(ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
    _series.GetOrAdd(new TagSet(tags), static _ => new Cell());

  private IEnumerable<Measurement<T>> _observe() {
    yield return new Measurement<T>(_untagged.Value);
    foreach (var (tags, cell) in _series) {
      yield return new Measurement<T>(cell.Value, tags.Tags);
    }
  }

  /// <summary>One accumulated count. A lock, not an interlocked, so every numeric T works.</summary>
  private sealed class Cell {
    private readonly Lock _sync = new();
    private T _value;

    public T Value {
      get {
        lock (_sync) {
          return _value;
        }
      }
    }

    public void Add(T delta) {
      lock (_sync) {
        _value += delta;
      }
    }
  }

  /// <summary>A tag set as a dictionary key: order-insensitive, compared by key and value text.</summary>
  private sealed class TagSet : IEquatable<TagSet> {
    private readonly int _hash;

    public TagSet(ReadOnlySpan<KeyValuePair<string, object?>> tags) {
      var array = tags.ToArray();
      Array.Sort(array, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
      Tags = array;
      var hash = new HashCode();
      foreach (var tag in array) {
        hash.Add(tag.Key, StringComparer.Ordinal);
        hash.Add(tag.Value?.ToString(), StringComparer.Ordinal);
      }
      _hash = hash.ToHashCode();
    }

    public KeyValuePair<string, object?>[] Tags { get; }

    public bool Equals(TagSet? other) {
      if (other is null || other.Tags.Length != Tags.Length || other._hash != _hash) {
        return false;
      }
      for (var i = 0; i < Tags.Length; i++) {
        if (!string.Equals(Tags[i].Key, other.Tags[i].Key, StringComparison.Ordinal)
            || !string.Equals(Tags[i].Value?.ToString(), other.Tags[i].Value?.ToString(), StringComparison.Ordinal)) {
          return false;
        }
      }
      return true;
    }

    public override bool Equals(object? obj) => Equals(obj as TagSet);

    public override int GetHashCode() => _hash;
  }
}

/// <summary>Creates passive counters on a meter (see <see cref="PassiveCounter{T}"/>).</summary>
/// <docs>operations/observability/metrics</docs>
public static class PassiveCounterMeterExtensions {
  /// <summary>Creates a monotonic passive counter: the process accumulates, the meter observes.</summary>
  /// <typeparam name="T">The count's numeric type.</typeparam>
  /// <param name="meter">The meter to register on.</param>
  /// <param name="name">The instrument name.</param>
  /// <param name="unit">The optional unit.</param>
  /// <param name="description">The optional description.</param>
  public static PassiveCounter<T> CreatePassiveCounter<T>(this Meter meter, string name, string? unit = null, string? description = null)
      where T : struct, INumberBase<T> =>
    new(meter, name, unit, description, monotonic: true);

  /// <summary>Creates a passive up-down counter: the process accumulates, the meter observes.</summary>
  /// <typeparam name="T">The count's numeric type.</typeparam>
  /// <param name="meter">The meter to register on.</param>
  /// <param name="name">The instrument name.</param>
  /// <param name="unit">The optional unit.</param>
  /// <param name="description">The optional description.</param>
  public static PassiveCounter<T> CreatePassiveUpDownCounter<T>(this Meter meter, string name, string? unit = null, string? description = null)
      where T : struct, INumberBase<T> =>
    new(meter, name, unit, description, monotonic: false);
}
