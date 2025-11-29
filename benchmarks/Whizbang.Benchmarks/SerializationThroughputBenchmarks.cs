using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Whizbang.Core.Observability;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Benchmarks;

/// <summary>
/// Serialization throughput benchmarks measuring sustained serialization/deserialization rates.
/// Focuses on JSON serialization performance with varying message sizes.
/// </summary>
[MemoryDiagnoser]
[MinColumn, MaxColumn]
public class SerializationThroughputBenchmarks {
  private IMessageSerializer _serializer = null!;

  // Test message types
  private record TinyMessage(int Value);
  private record SmallMessage(string Id, int Value, string Name);
  private record MediumMessage(string Id, int Value, string Name, string Description, DateTime Timestamp);
  private record LargeMessage(string Id, int Value, string Name, string Description, DateTime Timestamp, string Payload);

  private List<IMessageEnvelope> _tinyEnvelopes = null!;
  private List<IMessageEnvelope> _smallEnvelopes = null!;
  private List<IMessageEnvelope> _mediumEnvelopes = null!;
  private List<IMessageEnvelope> _largeEnvelopes = null!;

  private List<byte[]> _serializedTiny = null!;
  private List<byte[]> _serializedSmall = null!;
  private List<byte[]> _serializedMedium = null!;
  private List<byte[]> _serializedLarge = null!;

  [GlobalSetup]
  public void Setup() {
    _serializer = new JsonMessageSerializer(new Whizbang.Core.Generated.WhizbangJsonContext());

    // Pre-generate test envelopes
    _tinyEnvelopes = [.. Enumerable.Range(0, 100_000).Select(i => _createEnvelope(new TinyMessage(i)))];

    _smallEnvelopes = [.. Enumerable.Range(0, 100_000).Select(i => _createEnvelope(new SmallMessage($"msg-{i}", i, $"name-{i}")))];

    _mediumEnvelopes = [.. Enumerable.Range(0, 100_000)
      .Select(i => _createEnvelope(new MediumMessage(
        $"msg-{i}",
        i,
        $"name-{i}",
        $"This is a medium message with description {i}",
        DateTime.UtcNow.AddSeconds(i))))];

    _largeEnvelopes = [.. Enumerable.Range(0, 10_000)
      .Select(i => _createEnvelope(new LargeMessage(
        $"msg-{i}",
        i,
        $"name-{i}",
        $"This is a large message with description {i}",
        DateTime.UtcNow.AddSeconds(i),
        new string('x', 10_000))))];

    // Pre-serialize for deserialization benchmarks
    _serializedTiny = [.. _tinyEnvelopes.Select(e => _serializer.SerializeAsync(e).GetAwaiter().GetResult())];
    _serializedSmall = [.. _smallEnvelopes.Select(e => _serializer.SerializeAsync(e).GetAwaiter().GetResult())];
    _serializedMedium = [.. _mediumEnvelopes.Select(e => _serializer.SerializeAsync(e).GetAwaiter().GetResult())];
    _serializedLarge = [.. _largeEnvelopes.Select(e => _serializer.SerializeAsync(e).GetAwaiter().GetResult())];
  }

  /// <summary>
  /// Baseline: Serialize 100K tiny messages (minimal payload).
  /// </summary>
  [Benchmark(Baseline = true)]
  [Arguments(100_000)]
  public async Task Serialize_100K_TinyMessagesAsync(int count) {
    for (int i = 0; i < count; i++) {
      var envelope = _tinyEnvelopes[i];
      await _serializer.SerializeAsync(envelope);
    }
  }

  /// <summary>
  /// Serialize 100K small messages (typical command/event size).
  /// </summary>
  [Benchmark]
  [Arguments(100_000)]
  public async Task Serialize_100K_SmallMessagesAsync(int count) {
    for (int i = 0; i < count; i++) {
      var envelope = _smallEnvelopes[i];
      await _serializer.SerializeAsync(envelope);
    }
  }

  /// <summary>
  /// Serialize 100K medium messages (realistic business message size).
  /// </summary>
  [Benchmark]
  [Arguments(100_000)]
  public async Task Serialize_100K_MediumMessagesAsync(int count) {
    for (int i = 0; i < count; i++) {
      var envelope = _mediumEnvelopes[i];
      await _serializer.SerializeAsync(envelope);
    }
  }

  /// <summary>
  /// Serialize 10K large messages (10KB payloads).
  /// </summary>
  [Benchmark]
  [Arguments(10_000)]
  public async Task Serialize_10K_LargeMessagesAsync(int count) {
    for (int i = 0; i < count; i++) {
      var envelope = _largeEnvelopes[i];
      await _serializer.SerializeAsync(envelope);
    }
  }

  /// <summary>
  /// Deserialize 100K tiny messages.
  /// </summary>
  [Benchmark]
  [Arguments(100_000)]
  public async Task Deserialize_100K_TinyMessagesAsync(int count) {
    for (int i = 0; i < count; i++) {
      var bytes = _serializedTiny[i];
      await _serializer.DeserializeAsync<TinyMessage>(bytes);
    }
  }

  /// <summary>
  /// Deserialize 100K small messages.
  /// </summary>
  [Benchmark]
  [Arguments(100_000)]
  public async Task Deserialize_100K_SmallMessagesAsync(int count) {
    for (int i = 0; i < count; i++) {
      var bytes = _serializedSmall[i];
      await _serializer.DeserializeAsync<SmallMessage>(bytes);
    }
  }

  /// <summary>
  /// Deserialize 100K medium messages.
  /// </summary>
  [Benchmark]
  [Arguments(100_000)]
  public async Task Deserialize_100K_MediumMessagesAsync(int count) {
    for (int i = 0; i < count; i++) {
      var bytes = _serializedMedium[i];
      await _serializer.DeserializeAsync<MediumMessage>(bytes);
    }
  }

  /// <summary>
  /// Deserialize 10K large messages.
  /// </summary>
  [Benchmark]
  [Arguments(10_000)]
  public async Task Deserialize_10K_LargeMessagesAsync(int count) {
    for (int i = 0; i < count; i++) {
      var bytes = _serializedLarge[i];
      await _serializer.DeserializeAsync<LargeMessage>(bytes);
    }
  }

  /// <summary>
  /// Round-trip: Serialize and deserialize 50K messages.
  /// Measures complete serialization cycle overhead.
  /// </summary>
  [Benchmark]
  [Arguments(50_000)]
  public async Task RoundTrip_50K_SmallMessagesAsync(int count) {
    for (int i = 0; i < count; i++) {
      var envelope = _smallEnvelopes[i];
      var bytes = await _serializer.SerializeAsync(envelope);
      await _serializer.DeserializeAsync<SmallMessage>(bytes);
    }
  }

  /// <summary>
  /// Round-trip with medium messages.
  /// </summary>
  [Benchmark]
  [Arguments(50_000)]
  public async Task RoundTrip_50K_MediumMessagesAsync(int count) {
    for (int i = 0; i < count; i++) {
      var envelope = _mediumEnvelopes[i];
      var bytes = await _serializer.SerializeAsync(envelope);
      await _serializer.DeserializeAsync<MediumMessage>(bytes);
    }
  }

  /// <summary>
  /// Serialize with complex metadata (10 entries).
  /// </summary>
  [Benchmark]
  [Arguments(50_000)]
  public async Task Serialize_50K_WithComplexMetadataAsync(int count) {
    var envelopes = Enumerable.Range(0, count)
      .Select(i => {
        var envelope = new MessageEnvelope<SmallMessage> {
          MessageId = MessageId.New(),
          Payload = new SmallMessage($"msg-{i}", i, $"name-{i}"),
          Hops = []
        };

        envelope.AddHop(new MessageHop {
          Type = HopType.Current,
          ServiceName = "BenchmarkService",
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = CorrelationId.New(),
          CausationId = MessageId.New(),
          Metadata = new Dictionary<string, object> {
            ["key1"] = $"value1-{i}",
            ["key2"] = i,
            ["key3"] = DateTime.UtcNow,
            ["key4"] = true,
            ["key5"] = $"value5-{i}",
            ["key6"] = i * 2,
            ["key7"] = $"value7-{i}",
            ["key8"] = i * 3,
            ["key9"] = $"value9-{i}",
            ["key10"] = i * 4
          }
        });

        return envelope;
      })
      .ToList();

    for (int i = 0; i < count; i++) {
      await _serializer.SerializeAsync(envelopes[i]);
    }
  }

  /// <summary>
  /// Serialize with multiple hops (3 hops - current + 2 causation).
  /// </summary>
  [Benchmark]
  [Arguments(50_000)]
  public async Task Serialize_50K_WithMultipleHopsAsync(int count) {
    var envelopes = Enumerable.Range(0, count)
      .Select(i => _createEnvelopeWithMultipleHops(new SmallMessage($"msg-{i}", i, $"name-{i}")))
      .ToList();

    for (int i = 0; i < count; i++) {
      await _serializer.SerializeAsync(envelopes[i]);
    }
  }

  /// <summary>
  /// Parallel serialization throughput (10 concurrent serializers).
  /// </summary>
  [Benchmark]
  [Arguments(100_000, 10)]
  public async Task Serialize_Parallel_100K_MessagesAsync(int totalCount, int parallelism) {
    var messagesPerThread = totalCount / parallelism;

    var tasks = Enumerable.Range(0, parallelism)
      .Select(async threadId => {
        var start = threadId * messagesPerThread;
        for (int i = 0; i < messagesPerThread; i++) {
          var index = start + i;
          await _serializer.SerializeAsync(_smallEnvelopes[index]);
        }
      });

    await Task.WhenAll(tasks);
  }

  /// <summary>
  /// Parallel deserialization throughput (10 concurrent deserializers).
  /// </summary>
  [Benchmark]
  [Arguments(100_000, 10)]
  public async Task Deserialize_Parallel_100K_MessagesAsync(int totalCount, int parallelism) {
    var messagesPerThread = totalCount / parallelism;

    var tasks = Enumerable.Range(0, parallelism)
      .Select(async threadId => {
        var start = threadId * messagesPerThread;
        for (int i = 0; i < messagesPerThread; i++) {
          var index = start + i;
          await _serializer.DeserializeAsync<SmallMessage>(_serializedSmall[index]);
        }
      });

    await Task.WhenAll(tasks);
  }

  private static IMessageEnvelope _createEnvelope<T>(T payload) {
    var envelope = new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = []
    };

    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceName = "BenchmarkService",
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    });

    return envelope;
  }

  private static IMessageEnvelope _createEnvelopeWithMultipleHops<T>(T payload) {
    var envelope = new MessageEnvelope<T> {
      MessageId = MessageId.New(),
      Payload = payload,
      Hops = []
    };

    // Add current hop
    envelope.AddHop(new MessageHop {
      Type = HopType.Current,
      ServiceName = "CurrentService",
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    });

    // Add causation hops (simulating message chain)
    envelope.AddHop(new MessageHop {
      Type = HopType.Causation,
      ServiceName = "ParentService",
      Timestamp = DateTimeOffset.UtcNow.AddMilliseconds(-100),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    });

    envelope.AddHop(new MessageHop {
      Type = HopType.Causation,
      ServiceName = "OriginService",
      Timestamp = DateTimeOffset.UtcNow.AddMilliseconds(-200),
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New()
    });

    return envelope;
  }
}
