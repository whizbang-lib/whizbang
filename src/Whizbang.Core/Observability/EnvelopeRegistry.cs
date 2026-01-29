using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Whizbang.Core.Observability;

/// <summary>
/// Pooled dictionary implementation for tracking message envelopes by their payload.
/// Uses object reference identity (not value equality) for lookups.
/// After initial warmup, operations are zero-allocation.
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses a static pool of dictionaries to minimize allocations.
/// Each EnvelopeRegistry instance rents a dictionary from the pool and returns
/// it on Dispose(). The dictionary uses <see cref="ReferenceEqualityComparer"/>
/// to ensure exact reference matching for message lookups.
/// </para>
/// <para>
/// Thread-safety is achieved via explicit locking rather than ConcurrentDictionary
/// to enable pooling. The lock overhead is minimal for typical message processing
/// scenarios where contention is low.
/// </para>
/// </remarks>
/// <docs>core-concepts/envelope-registry</docs>
/// <tests>tests/Whizbang.Observability.Tests/EnvelopeRegistryTests.cs:Register_WithEnvelope_CanBeRetrievedByMessageAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/EnvelopeRegistryTests.cs:TryGetEnvelope_WithUnregisteredMessage_ReturnsNullAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/EnvelopeRegistryTests.cs:TryGetEnvelope_WithDifferentInstanceSameValue_ReturnsNullAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/EnvelopeRegistryTests.cs:Unregister_ByMessage_RemovesFromRegistryAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/EnvelopeRegistryTests.cs:Unregister_ByEnvelope_RemovesFromRegistryAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/EnvelopeRegistryTests.cs:Register_MultipleEnvelopes_AllCanBeRetrievedAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/EnvelopeRegistryTests.cs:Dispose_ClearsRegistryAndReturnsDictionaryToPoolAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/EnvelopeRegistryTests.cs:Register_SameMessageTwice_OverwritesPreviousEnvelopeAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/EnvelopeRegistryTests.cs:Unregister_NonExistentMessage_DoesNotThrowAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/EnvelopeRegistryTests.cs:Registry_IsThreadSafe_ConcurrentAccessAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/EnvelopeRegistryTests.cs:PoolReuse_MultipleRegistryInstances_ReusesDictionariesAsync</tests>
public sealed class EnvelopeRegistry : IEnvelopeRegistry, IDisposable {
  private static readonly ConcurrentBag<Dictionary<object, IMessageEnvelope>> _pool = [];
  private static int _poolSize;
  private const int MAX_POOL_SIZE = 256;

  private readonly Dictionary<object, IMessageEnvelope> _entries;
  private readonly object _lock = new();

  /// <summary>
  /// Creates a new EnvelopeRegistry, renting a dictionary from the pool if available.
  /// </summary>
  public EnvelopeRegistry() {
    if (_pool.TryTake(out var dict)) {
      Interlocked.Decrement(ref _poolSize);
      _entries = dict;
    } else {
      _entries = new Dictionary<object, IMessageEnvelope>(ReferenceEqualityComparer.Instance);
    }
  }

  /// <inheritdoc />
  public void Register<T>(MessageEnvelope<T> envelope) {
    lock (_lock) {
      _entries[envelope.Payload!] = envelope;
    }
  }

  /// <inheritdoc />
  public MessageEnvelope<T>? TryGetEnvelope<T>(T message) where T : notnull {
    lock (_lock) {
      if (_entries.TryGetValue(message, out var envelope)) {
        return envelope as MessageEnvelope<T>;
      }
      return null;
    }
  }

  /// <inheritdoc />
  public void Unregister<T>(T message) where T : notnull {
    lock (_lock) {
      _entries.Remove(message);
    }
  }

  /// <inheritdoc />
  public void Unregister<T>(MessageEnvelope<T> envelope) {
    lock (_lock) {
      _entries.Remove(envelope.Payload!);
    }
  }

  /// <summary>
  /// Disposes the registry, clearing all entries and returning the dictionary to the pool.
  /// </summary>
  public void Dispose() {
    lock (_lock) {
      _entries.Clear();
    }

    if (_poolSize < MAX_POOL_SIZE) {
      _pool.Add(_entries);
      Interlocked.Increment(ref _poolSize);
    }
  }
}
