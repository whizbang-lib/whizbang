namespace Whizbang.Core.ValueObjects;

/// <summary>Fills a span with cryptographically strong random bytes.</summary>
internal delegate void RandomFill(Span<byte> destination);

/// <summary>
/// Issues UUIDv7 ids that sort in the order they were issued: every id sorts after every id issued before it,
/// compared as big-endian bytes, which is how the string form, the wire and PostgreSQL's <c>uuid</c> type order
/// them. Event, cursor and claim ordering across the framework rests on that one property.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Layout</strong> (RFC 9562 version 7, with the fixed-length dedicated counter of section 6.2, method 1):
/// </para>
/// <code>
///  bits   0..47   unix time in milliseconds
///  bits  48..51   version (0111)
///  bits  52..63   counter, high 12 bits
///  bits  64..65   variant (10)
///  bits  66..79   counter, low 14 bits
///  bits  80..127  random
/// </code>
/// <para>
/// The 26-bit counter is what orders ids within one millisecond. The first id of a millisecond seeds it from 25
/// random bits (the top bit clear, so at least 2^25 of counter space remains); each further id in the same
/// millisecond adds a random step of 1 to 16, which keeps the next id hard to guess without costing the order.
/// </para>
/// <para>
/// Two edge cases are handled explicitly:
/// </para>
/// <list type="bullet">
/// <item>When the counter cannot take another step, the generator moves to the next millisecond and reseeds,
/// rather than letting the counter wrap to a smaller value inside the same millisecond.</item>
/// <item>When the clock goes backwards, the generator keeps issuing in the last millisecond it used and keeps
/// counting, rather than stepping its timestamp forward once per clock reading. It resumes real time once the
/// clock passes that millisecond again.</item>
/// </list>
/// <para>
/// Every id is issued under one lock, so the order ids leave the generator is their sort order even across
/// threads. Randomness is drawn from the random source in bulk into a buffer the lock also guards.
/// </para>
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#uuid7-generator</docs>
/// <tests>tests/Whizbang.Core.Tests/ValueObjects/Uuid7GeneratorTests.cs</tests>
internal sealed class Uuid7Generator {
  private const int COUNTER_BITS = 26;
  private const uint COUNTER_MAX = (1u << COUNTER_BITS) - 1;
  private const uint SEED_MASK = (1u << (COUNTER_BITS - 1)) - 1;
  private const int RANDOM_BUFFER_SIZE = 4096;
  private const int TAIL_BYTES = 6;
  private static readonly long _maxRepresentableMillisecond = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

  /// <summary>The process-wide generator: the system clock and the operating system's random source.</summary>
  internal static readonly Uuid7Generator Shared = new(
    static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    System.Security.Cryptography.RandomNumberGenerator.Fill);

  private readonly Func<long> _unixMilliseconds;
  private readonly RandomFill _fillRandom;
  private readonly Action<Guid>? _onIssued;
  private readonly Lock _lock = new();
  private readonly byte[] _random = new byte[RANDOM_BUFFER_SIZE];
  private int _randomIndex = RANDOM_BUFFER_SIZE;
  private long _lastMillisecond = long.MinValue;
  private uint _counter;

  /// <summary>Creates a generator over the given clock and random source.</summary>
  /// <param name="unixMilliseconds">The current time, in milliseconds since the Unix epoch.</param>
  /// <param name="fillRandom">Fills a span with random bytes.</param>
  /// <param name="onIssued">Called with each id while the issuing lock is still held; lets a test observe issue order.</param>
  internal Uuid7Generator(Func<long> unixMilliseconds, RandomFill fillRandom, Action<Guid>? onIssued = null) {
    _unixMilliseconds = unixMilliseconds;
    _fillRandom = fillRandom;
    _onIssued = onIssued;
  }

  /// <summary>Issues the next id.</summary>
  internal Guid NewGuid() {
    Span<byte> bytes = stackalloc byte[16];
    lock (_lock) {
      var now = _unixMilliseconds();
      if (now > _lastMillisecond) {
        _startMillisecond(now);
      } else {
        var step = 1u + (uint)(_nextRandomByte() & 0x0F);
        if (_counter > COUNTER_MAX - step) {
          _startMillisecond(_lastMillisecond + 1);
        } else {
          _counter += step;
        }
      }

      var ms = _lastMillisecond;
      bytes[0] = (byte)(ms >> 40);
      bytes[1] = (byte)(ms >> 32);
      bytes[2] = (byte)(ms >> 24);
      bytes[3] = (byte)(ms >> 16);
      bytes[4] = (byte)(ms >> 8);
      bytes[5] = (byte)ms;
      bytes[6] = (byte)(0x70 | ((_counter >> 22) & 0x0F));
      bytes[7] = (byte)(_counter >> 14);
      bytes[8] = (byte)(0x80 | ((_counter >> 8) & 0x3F));
      bytes[9] = (byte)_counter;
      _nextRandomBytes(bytes.Slice(10, TAIL_BYTES));

      var id = new Guid(bytes, bigEndian: true);
      _onIssued?.Invoke(id);
      return id;
    }
  }

  /// <summary>
  /// Reads the millisecond a version 7 id carries in its first 48 bits, whichever generator produced it.
  /// Returns <see cref="DateTimeOffset.MinValue"/> for any other version, and for a millisecond past what
  /// <see cref="DateTimeOffset"/> can represent.
  /// </summary>
  internal static DateTimeOffset GetTimestamp(Guid id) {
    if (id.Version != 7) {
      return DateTimeOffset.MinValue;
    }
    Span<byte> bytes = stackalloc byte[16];
    id.TryWriteBytes(bytes, bigEndian: true, out _);
    var ms = ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
      | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
    // 48 bits reach the year 10889; DateTimeOffset stops at 9999.
    return ms > _maxRepresentableMillisecond ? DateTimeOffset.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(ms);
  }

  private void _startMillisecond(long millisecond) {
    _lastMillisecond = millisecond;
    Span<byte> seed = stackalloc byte[4];
    _nextRandomBytes(seed);
    _counter = (uint)((seed[0] << 24) | (seed[1] << 16) | (seed[2] << 8) | seed[3]) & SEED_MASK;
  }

  private byte _nextRandomByte() {
    Span<byte> one = stackalloc byte[1];
    _nextRandomBytes(one);
    return one[0];
  }

  private void _nextRandomBytes(Span<byte> destination) {
    if (_randomIndex + destination.Length > RANDOM_BUFFER_SIZE) {
      _fillRandom(_random);
      _randomIndex = 0;
    }
    _random.AsSpan(_randomIndex, destination.Length).CopyTo(destination);
    _randomIndex += destination.Length;
  }
}
