using System.Collections.Concurrent;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.ValueObjects;

/// <summary>
/// The framework's UUIDv7 generator. Everything that orders events, cursors and claims by id relies on one
/// property: an id issued later sorts after every id issued before it, compared the way PostgreSQL and the
/// string form compare them (big-endian bytes). These tests pin that property and the layout that carries it,
/// with a scripted clock and random source so the edge cases (same millisecond, clock going backwards, counter
/// exhaustion) are exercised exactly rather than hoped for.
/// </summary>
[Category("Core")]
[Category("ValueObjects")]
[Category("IdGeneration")]
public class Uuid7GeneratorTests {
  private const long T0 = 1_790_000_000_000; // an ordinary 2026 unix millisecond

  /// <summary>A clock the test moves by hand.</summary>
  private sealed class ManualClock(long start) {
    public long Now { get; set; } = start;
  }

  /// <summary>A random source that returns the same byte everywhere, and counts refills.</summary>
  private sealed class ConstantRandom(byte value) {
    public byte Value { get; set; } = value;
    public int Fills { get; private set; }
    public void Fill(Span<byte> destination) {
      Fills++;
      destination.Fill(Value);
    }
  }

  /// <summary>
  /// A random source that plays back a script, then zeros. The generator draws its randomness in bulk, so the
  /// script is laid out in the order the generator consumes it: a new millisecond takes 4 seed bytes then 6 tail
  /// bytes; each further id in the same millisecond takes 1 step byte then 6 tail bytes.
  /// </summary>
  private sealed class ScriptedRandom(params byte[] script) {
    private int _position;
    public void Fill(Span<byte> destination) {
      for (int i = 0; i < destination.Length; i++) {
        destination[i] = _position < script.Length ? script[_position++] : (byte)0x00;
      }
    }
  }

  private static byte[] _repeat(byte value, int count) => Enumerable.Repeat(value, count).ToArray();

  private static (Uuid7Generator Generator, ManualClock Clock, ConstantRandom Random) _create(byte randomByte = 0x00, long start = T0) {
    var clock = new ManualClock(start);
    var random = new ConstantRandom(randomByte);
    return (new Uuid7Generator(() => clock.Now, random.Fill), clock, random);
  }

  private static byte[] _bytes(Guid id) => id.ToByteArray(bigEndian: true);

  private static long _millisecond(Guid id) {
    var b = _bytes(id);
    return ((long)b[0] << 40) | ((long)b[1] << 32) | ((long)b[2] << 24) | ((long)b[3] << 16) | ((long)b[4] << 8) | b[5];
  }

  private static uint _counter(Guid id) {
    var b = _bytes(id);
    return ((uint)(b[6] & 0x0F) << 22) | ((uint)b[7] << 14) | ((uint)(b[8] & 0x3F) << 8) | b[9];
  }

  /// <summary>Big-endian byte order: the order of the string form and of PostgreSQL's uuid type.</summary>
  private static int _compare(Guid a, Guid b) => _bytes(a).AsSpan().SequenceCompareTo(_bytes(b));

  // ── Layout ────────────────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task NewGuid_IsVersion7WithTheRfcVariantAsync() {
    var (generator, _, _) = _create(0xFF);

    var id = generator.NewGuid();

    await Assert.That(id.Version).IsEqualTo(7);
    await Assert.That(_bytes(id)[8] & 0xC0).IsEqualTo(0x80);
  }

  [Test]
  public async Task NewGuid_CarriesTheClocksMillisecondInTheFirst48BitsAsync() {
    var (generator, _, _) = _create();

    var id = generator.NewGuid();

    await Assert.That(_millisecond(id)).IsEqualTo(T0);
    await Assert.That(Uuid7Generator.GetTimestamp(id)).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(T0));
  }

  [Test]
  public async Task NewGuid_FreshMillisecond_SeedsTheCounterFromRandomWithTheTopBitClearAsync() {
    // All-ones random would seed the full 26 bits; the seed keeps the top bit clear so a millisecond always has
    // at least 2^25 of counter space left to count up through.
    var (generator, _, _) = _create(0xFF);

    var id = generator.NewGuid();

    await Assert.That(_counter(id)).IsEqualTo((1u << 25) - 1);
  }

  [Test]
  public async Task NewGuid_TakesTheLast48BitsFromTheRandomSourceAsync() {
    var (generator, _, _) = _create(0xA5);

    var id = generator.NewGuid();

    await Assert.That(_bytes(id)[10..].All(b => b == 0xA5)).IsTrue();
  }

  [Test]
  public async Task NewGuid_StringFormMatchesTheBigEndianBytesAsync() {
    // The string form, the wire and PostgreSQL all read the id as big-endian bytes; the generator must build the
    // Guid so that .NET's own formatting agrees with that order.
    var (generator, _, _) = _create(0x11);

    var id = generator.NewGuid();

    await Assert.That(id.ToString("N")).IsEqualTo(Convert.ToHexString(_bytes(id)).ToLowerInvariant());
  }

  // ── Ordering ──────────────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task NewGuid_SameMillisecond_EachIdSortsAfterThePreviousAsync() {
    var (generator, _, random) = _create(0x00);
    var previous = generator.NewGuid();

    for (int i = 0; i < 1_000; i++) {
      random.Value = (byte)i; // vary the step and the tail
      var next = generator.NewGuid();
      await Assert.That(_millisecond(next)).IsEqualTo(T0);
      await Assert.That(_compare(next, previous)).IsGreaterThan(0);
      previous = next;
    }
  }

  [Test]
  public async Task NewGuid_SameMillisecond_StepsTheCounterByOneToSixteenAsync() {
    var clock = new ManualClock(T0);
    var random = new ScriptedRandom([
      .. _repeat(0x00, 4), .. _repeat(0x00, 6),  // first id: seed 0
      0x00, .. _repeat(0x00, 6),                  // second: step byte low nibble 0 → step 1
      0x0F, .. _repeat(0x00, 6)]);                // third: low nibble 15 → step 16
    var generator = new Uuid7Generator(() => clock.Now, random.Fill);

    var first = generator.NewGuid();
    var second = generator.NewGuid();
    var third = generator.NewGuid();

    await Assert.That(_counter(first)).IsEqualTo(0u);
    await Assert.That(_counter(second) - _counter(first)).IsEqualTo(1u);
    await Assert.That(_counter(third) - _counter(second)).IsEqualTo(16u);
  }

  [Test]
  public async Task NewGuid_TheTailNeverDecidesTheOrderWithinAMillisecondAsync() {
    // A descending tail must not reverse the order: the counter sits above it.
    var clock = new ManualClock(T0);
    var random = new ScriptedRandom([
      .. _repeat(0x00, 4), .. _repeat(0xFF, 6),   // first id: seed 0, tail all ones
      0x00, .. _repeat(0x00, 6)]);                 // second: step 1, tail all zeros
    var generator = new Uuid7Generator(() => clock.Now, random.Fill);

    var first = generator.NewGuid();
    var second = generator.NewGuid();

    await Assert.That(_bytes(first)[10..].All(b => b == 0xFF)).IsTrue();
    await Assert.That(_bytes(second)[10..].All(b => b == 0x00)).IsTrue();
    await Assert.That(_compare(second, first)).IsGreaterThan(0);
  }

  [Test]
  public async Task NewGuid_LaterMillisecond_SortsAfterEvenWithASmallerCounterAsync() {
    var clock = new ManualClock(T0);
    var random = new ScriptedRandom([
      .. _repeat(0xFF, 4), .. _repeat(0x00, 6),   // first id: the largest seed
      .. _repeat(0x00, 4), .. _repeat(0x00, 6)]); // next millisecond: the smallest seed
    var generator = new Uuid7Generator(() => clock.Now, random.Fill);
    var first = generator.NewGuid();

    clock.Now = T0 + 1;
    var second = generator.NewGuid();

    await Assert.That(_millisecond(second)).IsEqualTo(T0 + 1);
    await Assert.That(_counter(second)).IsLessThan(_counter(first));
    await Assert.That(_compare(second, first)).IsGreaterThan(0);
  }

  [Test]
  public async Task NewGuid_ClockGoesBackwards_KeepsTheLastMillisecondAndKeepsCountingAsync() {
    var (generator, clock, _) = _create(0x00);
    var first = generator.NewGuid();

    clock.Now = T0 - 5_000;
    var second = generator.NewGuid();

    await Assert.That(_millisecond(second)).IsEqualTo(T0);
    await Assert.That(_compare(second, first)).IsGreaterThan(0);
  }

  [Test]
  public async Task NewGuid_ClockCatchesUpAfterGoingBackwards_ResumesRealTimeAsync() {
    var (generator, clock, _) = _create(0x00);
    generator.NewGuid();
    clock.Now = T0 - 5_000;
    var held = generator.NewGuid();

    clock.Now = T0 + 1;
    var resumed = generator.NewGuid();

    await Assert.That(_millisecond(resumed)).IsEqualTo(T0 + 1);
    await Assert.That(_compare(resumed, held)).IsGreaterThan(0);
  }

  [Test]
  public async Task NewGuid_CounterExhausted_BorrowsTheNextMillisecondInsteadOfWrappingAsync() {
    // Seed at the top of the seed range (2^25 - 1), then take the largest step until the 26-bit counter cannot
    // hold another: the id must move to the next millisecond, never wrap to a small counter in the same one.
    var (generator, _, _) = _create(0xFF);
    var previous = generator.NewGuid();
    long borrowedAt = -1;
    int outOfOrder = 0;

    for (int i = 0; i < 3_000_000 && borrowedAt < 0; i++) {
      var next = generator.NewGuid();
      if (_compare(next, previous) <= 0) {
        outOfOrder++;
      }
      if (_millisecond(next) != T0) {
        borrowedAt = _millisecond(next);
      }
      previous = next;
    }

    await Assert.That(outOfOrder).IsEqualTo(0);
    await Assert.That(borrowedAt).IsEqualTo(T0 + 1);
  }

  [Test]
  public async Task NewGuid_AfterBorrowing_RealTimeCatchingUpToTheBorrowedMillisecondKeepsOrderAsync() {
    var (generator, clock, _) = _create(0xFF);
    var previous = generator.NewGuid();
    while (_millisecond(previous) == T0) {
      previous = generator.NewGuid();
    }

    clock.Now = T0 + 1; // the clock reaches the millisecond the generator already borrowed
    var next = generator.NewGuid();

    await Assert.That(_millisecond(next)).IsEqualTo(T0 + 1);
    await Assert.That(_compare(next, previous)).IsGreaterThan(0);
  }

  // ── Randomness ────────────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task NewGuid_DrawsRandomnessInBulk_NotOncePerIdAsync() {
    var (generator, _, random) = _create(0x01);

    for (int i = 0; i < 100; i++) {
      generator.NewGuid();
    }

    await Assert.That(random.Fills).IsEqualTo(1);
  }

  [Test]
  public async Task NewGuid_RefillsTheRandomBufferWhenItRunsOutAsync() {
    var (generator, _, random) = _create(0x01);

    for (int i = 0; i < 10_000; i++) {
      generator.NewGuid();
    }

    await Assert.That(random.Fills).IsGreaterThan(1);
  }

  // ── The shared generator ──────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task Shared_UsesTheSystemClockAsync() {
    var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    var id = Uuid7Generator.Shared.NewGuid();

    var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    await Assert.That(_millisecond(id)).IsGreaterThanOrEqualTo(before);
    await Assert.That(_millisecond(id)).IsLessThanOrEqualTo(after + 1);
  }

  [Test]
  public async Task Shared_OneMillionIdsInATightLoop_AreStrictlyIncreasingAsync() {
    var previous = Uuid7Generator.Shared.NewGuid();
    int outOfOrder = 0;

    for (int i = 0; i < 1_000_000; i++) {
      var next = Uuid7Generator.Shared.NewGuid();
      if (_compare(next, previous) <= 0) {
        outOfOrder++;
      }
      previous = next;
    }

    await Assert.That(outOfOrder).IsEqualTo(0);
  }

  [Test]
  public async Task Shared_ManyThreadsAtOnce_AreUniqueAndIncreasingPerThreadAsync() {
    const int threads = 16;
    const int perThread = 20_000;
    var all = new ConcurrentBag<Guid>();
    var outOfOrder = 0;
    using var start = new ManualResetEventSlim(false);

    var workers = Enumerable.Range(0, threads).Select(_ => Task.Factory.StartNew(() => {
      start.Wait();
      var previous = Guid.Empty;
      for (int i = 0; i < perThread; i++) {
        var next = Uuid7Generator.Shared.NewGuid();
        if (previous != Guid.Empty && _compare(next, previous) <= 0) {
          Interlocked.Increment(ref outOfOrder);
        }
        all.Add(next);
        previous = next;
      }
    }, TaskCreationOptions.LongRunning)).ToArray();
    start.Set();
    await Task.WhenAll(workers);

    await Assert.That(outOfOrder).IsEqualTo(0);
    await Assert.That(all.Distinct().Count()).IsEqualTo(threads * perThread);
  }

  [Test]
  public async Task Shared_IssueOrderIsSortOrder_AcrossThreadsAsync() {
    // Every id is issued under one lock, so the order ids come out of the generator IS their sort order. Record
    // the issue order with a sequence taken inside the same critical section the generator uses (the generator
    // exposes its issue hook for exactly this test) and check that sorting by id reproduces it.
    const int threads = 8;
    const int perThread = 5_000;
    var issued = new ConcurrentQueue<(long Sequence, Guid Id)>();
    long sequence = 0;
    var generator = new Uuid7Generator(
      () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
      System.Security.Cryptography.RandomNumberGenerator.Fill,
      id => issued.Enqueue((Interlocked.Increment(ref sequence), id)));

    await Task.WhenAll(Enumerable.Range(0, threads).Select(_ => Task.Run(() => {
      for (int i = 0; i < perThread; i++) {
        generator.NewGuid();
      }
    })));

    var byIssue = issued.OrderBy(x => x.Sequence).Select(x => x.Id).ToList();
    var byId = issued.Select(x => x.Id).OrderBy(x => x, Comparer<Guid>.Create(_compare)).ToList();
    await Assert.That(byId.SequenceEqual(byIssue)).IsTrue();
  }

  // ── Timestamp reading ─────────────────────────────────────────────────────────────────────────────────

  [Test]
  public async Task GetTimestamp_NotVersion7_ReturnsMinValueAsync() {
    var v4 = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    await Assert.That(Uuid7Generator.GetTimestamp(v4)).IsEqualTo(DateTimeOffset.MinValue);
  }

  [Test]
  public async Task GetTimestamp_ReadsTheMillisecondFromAnyVersion7IdAsync() {
    // An id another generator produced (here .NET's own) is read the same way: the first 48 bits.
    var id = Guid.Parse("0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    await Assert.That(Uuid7Generator.GetTimestamp(id).ToUnixTimeMilliseconds()).IsEqualTo(0x0190a1b2c3d4);
  }

  [Test]
  public async Task GetTimestamp_MillisecondPastWhatDateTimeOffsetHolds_ReturnsMinValueAsync() {
    // 48 bits of milliseconds reach the year 10889; DateTimeOffset stops at 9999.
    var id = Guid.Parse("ffffffff-ffff-7fff-bfff-ffffffffffff");

    await Assert.That(Uuid7Generator.GetTimestamp(id)).IsEqualTo(DateTimeOffset.MinValue);
  }
}
