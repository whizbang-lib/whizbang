using System.Collections;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Cap enforcement reads the cache without assuming its size held still.
/// </summary>
/// <remarks>
/// <para>
/// Observed in a deployed service under a bulk import, swallowed by the dispatch worker's
/// continue-on-failure guard so a message's deserialization silently failed:
/// </para>
/// <code>
/// System.ArgumentException: The index is equal to or greater than the length of the array...
///   at ConcurrentDictionary`2...ICollection&lt;KeyValuePair&gt;.CopyTo(...)
///   at Enumerable.ICollectionToArray[TSource](ICollection`1 collection)
///   at Enumerable.SkipTakeOrderedIterator`1.MoveNext()
///   at InboxDeserializeCache._enforceCapIfNeeded()
/// </code>
/// <para>
/// The mechanism is not the sort and not the cap: it is how LINQ materializes a source that
/// implements <see cref="ICollection{T}"/>. Ordering with a <c>Take</c> buffers the source, and
/// buffering an <c>ICollection</c> reads <c>Count</c>, allocates an array of exactly that size, and
/// then calls <c>CopyTo</c>. A concurrent dictionary that gained an entry in between hands
/// <c>CopyTo</c> more elements than the array can hold and it throws. The eviction lock does not
/// help, because it serializes enforcers against each other and not against the writers on the hot
/// path.
/// </para>
/// <para>
/// So the reproduction here does not race for that window; it removes the race. A source whose
/// <c>Count</c> is read and then grows before the copy fails the same way every single time, which
/// is what makes this a reproduction rather than a lucky run. The concurrency test below then
/// asserts the property the fix has to hold under real threads, and it asserts completion and the
/// cap rather than the absence of a rare event, because once the defect is fixed a test that waits
/// for a rare failure proves nothing.
/// </para>
/// </remarks>
[Category("Core")]
[Category("Workers")]
public class InboxDeserializeCacheCapConcurrencyTests {
  private const int CAP = 200;

  /// <summary>
  /// A source that behaves exactly as a concurrent dictionary being written to during a copy: its
  /// count is read, and by the time the copy runs there is one more element than the caller sized
  /// its array for.
  /// </summary>
  /// <remarks>
  /// This is not a hostile mock inventing an impossible state. It is the state
  /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/> is in whenever
  /// an insert lands between a reader's <c>Count</c> and its <c>CopyTo</c>, which under load is
  /// often. Growing on the <c>Count</c> read is how that becomes deterministic instead of timing
  /// dependent.
  /// </remarks>
  private sealed class GrowsBetweenCountAndCopy : ICollection<KeyValuePair<Guid, InboxDeserializeCache.Entry>> {
    private readonly List<KeyValuePair<Guid, InboxDeserializeCache.Entry>> _items = [];

    public GrowsBetweenCountAndCopy(int initial) {
      for (var i = 0; i < initial; i++) {
        _items.Add(_entry(i));
      }
    }

    private static KeyValuePair<Guid, InboxDeserializeCache.Entry> _entry(int i) =>
      new(Guid.NewGuid(), new InboxDeserializeCache.Entry(
        $"payload-{i}", DateTimeOffset.UnixEpoch.AddSeconds(i)));

    /// <summary>
    /// Reports the size the caller will size its array from, and THEN grows, which is the ordering
    /// that matters: the reader allocates for what it saw, and the copy finds one more.
    /// </summary>
    public int Count {
      get {
        var observed = _items.Count;
        _items.Add(_entry(observed));
        return observed;
      }
    }

    /// <summary>
    /// The concurrent dictionary's own contract: it refuses to copy into an array that cannot hold
    /// what it currently has, rather than truncating.
    /// </summary>
    public void CopyTo(KeyValuePair<Guid, InboxDeserializeCache.Entry>[] array, int index) {
      ArgumentNullException.ThrowIfNull(array);
      if (array.Length - index < _items.Count) {
        throw new ArgumentException(
          "The index is equal to or greater than the length of the array, or the number of elements "
          + "in the dictionary is greater than the available space from index to the end of the "
          + "destination array.", nameof(index));
      }
      _items.CopyTo(array, index);
    }

    // Enumeration is the safe face and the one the fix has to use: it walks what is there without
    // asking how much there is.
    public IEnumerator<KeyValuePair<Guid, InboxDeserializeCache.Entry>> GetEnumerator() =>
      _items.ToList().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsReadOnly => true;
    public void Add(KeyValuePair<Guid, InboxDeserializeCache.Entry> item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(KeyValuePair<Guid, InboxDeserializeCache.Entry> item) => throw new NotSupportedException();
    public bool Remove(KeyValuePair<Guid, InboxDeserializeCache.Entry> item) => throw new NotSupportedException();
  }

  [Test]
  public async Task SelectEvictionKeys_SourceGrewSinceItsCountWasRead_StillSelectsAsync() {
    var entries = new GrowsBetweenCountAndCopy(CAP);

    var keys = InboxDeserializeCache.SelectEvictionKeys(entries, CAP / 10);

    await Assert.That(keys.Length).IsEqualTo(CAP / 10)
      .Because("cap enforcement has to name a batch to evict even while the cache is being written "
        + "to, because being written to is the only condition under which the cap is ever reached. "
        + "Reading the source's size and then copying into an array sized from it throws the moment "
        + "an insert lands in between, and the dispatch worker swallows that as a failed "
        + "deserialization, so a message silently stops being processed under exactly the load that "
        + "caused it");
  }

  [Test]
  public async Task SelectEvictionKeys_SourceGrewSinceItsCountWasRead_SelectsTheOldestAsync() {
    // The selection still has to mean something: a fix that returned an arbitrary batch would pass
    // the test above and quietly evict the entries most likely to be needed next.
    var entries = new GrowsBetweenCountAndCopy(CAP);
    var oldest = entries.Take(CAP / 10).Select(static p => p.Key).ToHashSet();

    var keys = InboxDeserializeCache.SelectEvictionKeys(entries, CAP / 10);

    await Assert.That(keys.ToHashSet().IsSubsetOf(oldest)).IsTrue()
      .Because("the batch has to be the oldest entries by expiry; the fixture's expiries ascend with "
        + "insertion order, so the first entries are the oldest. Evicting by expiry is what makes the "
        + "cache an LRU rather than a random dropper");
  }

  [Test]
  public async Task SelectEvictionKeys_BatchLargerThanTheSource_SelectsWhatThereIsAsync() {
    // Best-effort by design: a cap that is briefly off by a few entries is fine, and asking for more
    // than exists must not throw or pad.
    var entries = new GrowsBetweenCountAndCopy(4);

    var keys = InboxDeserializeCache.SelectEvictionKeys(entries, 1000);

    await Assert.That(keys.Length).IsLessThanOrEqualTo(6)
      .Because("the source holds four entries and grows by one per size read, so a request for a "
        + "thousand yields what is there rather than throwing or returning duplicates");
    await Assert.That(keys.Distinct().Count()).IsEqualTo(keys.Length)
      .Because("a key evicted twice would be a second TryRemove that silently does nothing, which "
        + "hides how far under the cap the eviction actually got");
  }

  /// <summary>
  /// The property the fix has to hold under real threads: the cache keeps taking writes while the
  /// cap is enforced, and stays near the cap.
  /// </summary>
  /// <remarks>
  /// This is deliberately NOT the reproduction. It asserts that the operation completes and the cap
  /// is respected within a stated tolerance, which is checkable every run; a test that asserted a
  /// rare failure sometimes occurs would be unfalsifiable once the defect is fixed. The tolerance is
  /// explicit because the cap is best-effort: enforcement and insertion interleave, so the resident
  /// count is allowed to overshoot between an insert and the eviction it triggers.
  /// </remarks>
  [Test]
  [Timeout(120000)]
  public async Task Set_ManyWritersWhileTheCapIsEnforced_CompletesAndStaysNearTheCapAsync(
      CancellationToken cancellationToken) {
    var cache = new InboxDeserializeCache(
      new SystemTimeProvider(new FakeTimeProvider(new DateTimeOffset(2026, 5, 7, 12, 0, 0, TimeSpan.Zero))),
      TimeSpan.FromMinutes(5),
      CAP);
    const int writers = 8;
    const int perWriter = 500;

    // Fill to the cap first, so every write below is one that triggers enforcement.
    for (var i = 0; i < CAP; i++) {
      cache.Set(Guid.NewGuid(), $"seed-{i}");
    }

    var barrier = new Barrier(writers);
    var work = Enumerable.Range(0, writers).Select(_ => Task.Run(() => {
      cancellationToken.ThrowIfCancellationRequested();
      // Every writer starts at the same moment, so the writes and the evictions they trigger
      // genuinely overlap rather than running one after another.
      barrier.SignalAndWait();
      for (var i = 0; i < perWriter; i++) {
        cache.Set(Guid.NewGuid(), "payload");
      }
    })).ToArray();

    await Task.WhenAll(work);

    // The cap is best-effort, so the assertion is a band rather than an equality: enforcement
    // evicts a tenth of the cap at a time and writers insert between an eviction and the next
    // count, so a transient overshoot of roughly one batch per writer is expected and fine.
    var tolerance = CAP + (writers * (CAP / 10)) + writers;
    await Assert.That(cache.Count).IsLessThanOrEqualTo(tolerance)
      .Because($"{writers} writers put {writers * perWriter} entries through a cache capped at {CAP}, "
        + $"and it holds {cache.Count}; the cap is best-effort but it still has to bound memory, which "
        + "is the only reason it exists");
    await Assert.That(cache.Count).IsGreaterThan(0)
      .Because("eviction must not empty the cache: a cache that evicts everything it is given is a "
        + "cache that never hits, and the four lifecycle stages would each re-deserialize");
  }
}
