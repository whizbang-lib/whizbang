// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:StoreAsync_WithNullEnvelope_ThrowsArgumentNullExceptionAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetByCorrelationAsync_WithNullCorrelationIdsInStore_FiltersThemOutAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithCircularReference_ProtectsAgainstInfiniteLoopAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithMissingParent_StopsWalkingUpChainAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithChildren_IncludesChildMessagesAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithMultiGenerationChildren_IncludesAllDescendantsAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithEmptyCausationId_StopsWalkingAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_SortsResultsByTimestampAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetByTimeRangeAsync_WithEnvelopesWithoutCurrentHop_UsesMinValueTimestampAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetByTimeRangeAsync_WithNoHops_UsesMinValueTimestampAsync</tests>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithCircularReferenceInChildrenTree_ProtectsAgainstInfiniteLoopAsync</tests>
/// In-memory implementation of ITraceStore for testing, development and short-lived diagnostics.
/// Thread-safe, not durable, and bounded: it keeps the most recent <see cref="Capacity"/> envelopes
/// and evicts the oldest first, so an opted-in host cannot grow without bound (#1601).
/// No store is registered by default; a host opts in with
/// <see cref="TraceStoreRegistration.AddWhizbangInMemoryTraceStore(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>.
/// </summary>
/// <docs>fundamentals/persistence/observability#trace-store-registration</docs>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreCapacityTests.cs</tests>
public class InMemoryTraceStore : ITraceStore {
  /// <summary>The number of envelopes a store keeps when no capacity is given.</summary>
  public const int DEFAULT_CAPACITY = 10_000;

  private readonly ConcurrentDictionary<MessageId, IMessageEnvelope> _traces = new();
  // Insertion order of the held ids, oldest first; written only under _storeGate
  private readonly Queue<MessageId> _order = new();
  private readonly Lock _storeGate = new();

  /// <summary>Creates a store that keeps the most recent <see cref="DEFAULT_CAPACITY"/> envelopes.</summary>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreCapacityTests.cs:Constructor_Parameterless_UsesTheDefaultCapacityAsync</tests>
  public InMemoryTraceStore() : this(DEFAULT_CAPACITY) { }

  /// <summary>Creates a store that keeps the most recent <paramref name="capacity"/> envelopes.</summary>
  /// <param name="capacity">The most envelopes held; must be positive.</param>
  /// <exception cref="ArgumentOutOfRangeException">The capacity is zero or negative.</exception>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreCapacityTests.cs:Constructor_CapacityNotPositive_ThrowsAsync</tests>
  public InMemoryTraceStore(int capacity) {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
    Capacity = capacity;
  }

  /// <summary>The most envelopes this store keeps; past it the oldest is evicted.</summary>
  /// <tests>tests/Whizbang.Observability.Tests/DependencyInjection/TraceStoreRegistrationTests.cs:AddWhizbangInMemoryTraceStore_WithCapacity_UsesItAsync</tests>
  public int Capacity { get; }

  /// <summary>The number of envelopes held now, never more than <see cref="Capacity"/>.</summary>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreCapacityTests.cs:StoreAsync_PastCapacity_EvictsTheOldestFirstAsync</tests>
  public int Count => _traces.Count;

  /// <summary>
  /// Stores a message envelope in the trace store.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:StoreAsync_WithNullEnvelope_ThrowsArgumentNullExceptionAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_StoreAndRetrieve_ShouldStoreAndRetrieveEnvelopeAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByCorrelation_ShouldReturnAllMessagesWithSameCorrelationIdAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetCausalChain_ShouldReturnMessageAndParentsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetCausalChain_ShouldReturnJustMessageWhenNoParentsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByTimeRange_ShouldReturnMessagesInRangeAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByTimeRange_ShouldReturnMessagesInChronologicalOrderAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_ConcurrentStores_ShouldHandleConcurrencyAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreCapacityTests.cs:StoreAsync_PastCapacity_EvictsTheOldestFirstAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreCapacityTests.cs:StoreAsync_SameMessageTwice_KeepsOneEntryAndEvictsNothingAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreCapacityTests.cs:Queries_AfterEviction_NeverReturnAnEvictedEnvelopeAsync</tests>
  public Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct = default) {
    ArgumentNullException.ThrowIfNull(envelope);

    lock (_storeGate) {
      // The first write for an id wins, and a repeat is not a new entry, so it evicts nothing
      if (_traces.TryAdd(envelope.MessageId, envelope)) {
        _order.Enqueue(envelope.MessageId);
        if (_order.Count > Capacity) {
          // Every query reads _traces, so removing the oldest here hides it from all of them
          _traces.TryRemove(_order.Dequeue(), out _);
        }
      }
    }
    return Task.CompletedTask;
  }

  /// <summary>
  /// Retrieves a message envelope by its message ID.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_StoreAndRetrieve_ShouldStoreAndRetrieveEnvelopeAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByMessageId_ShouldReturnNullForNonExistentTraceAsync</tests>
  public Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct = default) {
    _traces.TryGetValue(messageId, out var envelope);
    return Task.FromResult(envelope);
  }

  /// <summary>
  /// Retrieves all message envelopes with the specified correlation ID.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetByCorrelationAsync_WithNullCorrelationIdsInStore_FiltersThemOutAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByCorrelation_ShouldReturnAllMessagesWithSameCorrelationIdAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByCorrelation_ShouldReturnEmptyListWhenNoMatchesAsync</tests>
  public Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct = default) {
    var results = _traces.Values
      .Where(e => {
        var corrId = e.GetCorrelationId();
        return corrId != null && corrId.Equals(correlationId);
      })
      .OrderBy(e => e.GetMessageTimestamp())
      .ToList();

    return Task.FromResult(results);
  }

  /// <summary>
  /// Retrieves the complete causal chain for a message, including ancestors and descendants.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithCircularReference_ProtectsAgainstInfiniteLoopAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithMissingParent_StopsWalkingUpChainAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithChildren_IncludesChildMessagesAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithMultiGenerationChildren_IncludesAllDescendantsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithEmptyCausationId_StopsWalkingAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_SortsResultsByTimestampAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithCircularReferenceInChildrenTree_ProtectsAgainstInfiniteLoopAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetCausalChain_ShouldReturnMessageAndParentsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetCausalChain_ShouldReturnJustMessageWhenNoParentsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetCausalChain_ShouldReturnEmptyWhenMessageNotFoundAsync</tests>
  public Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken ct = default) {
    if (!_traces.TryGetValue(messageId, out var envelope)) {
      return Task.FromResult(new List<IMessageEnvelope>());
    }

    var chain = new HashSet<MessageId>();
    var results = new List<IMessageEnvelope>();

    // Add the message itself
    chain.Add(envelope.MessageId);
    results.Add(envelope);

    // Walk up the causation chain (parents)
    var currentCausationId = envelope.GetCausationId();
    while (currentCausationId is { Value: var guidValue } && guidValue != Guid.Empty) {
      // currentCausationId is the MessageId of the parent message
      if (chain.Contains(currentCausationId.Value)) {
        break; // Circular reference protection
      }

      if (_traces.TryGetValue(currentCausationId.Value, out var parent)) {
        chain.Add(currentCausationId.Value); // Add the parent's MessageId to the chain
        results.Add(parent);
        currentCausationId = parent.GetCausationId();
      } else {
        break;
      }
    }

    // Walk down the causation chain (children)
    var children = _traces.Values
      .Where(e => {
        var causationId = e.GetCausationId();
        return causationId != null &&
               causationId.Value != Guid.Empty &&
               causationId.Equals(messageId) &&
               !chain.Contains(e.MessageId);
      })
      .ToList();

    foreach (var child in children) {
      AddChildrenRecursive(child, chain, results);
    }

    // Sort by timestamp
    results.Sort((a, b) => {
      var aTime = a.Hops?.FirstOrDefault(h => h.Type == HopType.Current)?.Timestamp ?? DateTimeOffset.MinValue;
      var bTime = b.Hops?.FirstOrDefault(h => h.Type == HopType.Current)?.Timestamp ?? DateTimeOffset.MinValue;
      return aTime.CompareTo(bTime);
    });

    return Task.FromResult(results);
  }

  /// <summary>
  /// Recursively adds child messages to the causal chain.
  /// </summary>
  /// <remarks>
  /// Internal rather than private so the already-visited answer can be asserted. Both call sites
  /// filter candidates with <c>!chain.Contains(...)</c> and each envelope carries exactly one
  /// causation id, so a sibling can never turn out to be a descendant of an earlier sibling —
  /// nothing the store can be asked today re-enters with a message already in the chain. The
  /// guard is still what stands between a causation cycle written by a producer and a stack
  /// overflow inside a diagnostics query.
  /// </remarks>
  internal void AddChildrenRecursive(IMessageEnvelope message, HashSet<MessageId> chain, List<IMessageEnvelope> results) {
    if (chain.Contains(message.MessageId)) {
      return; // Circular reference protection
    }

    chain.Add(message.MessageId);
    results.Add(message);

    // Find children of this message
    var children = _traces.Values
      .Where(e => {
        var causationId = e.GetCausationId();
        return causationId != null &&
               causationId.Value != Guid.Empty &&
               causationId.Equals(message.MessageId) &&
               !chain.Contains(e.MessageId);
      })
      .ToList();

    foreach (var child in children) {
      AddChildrenRecursive(child, chain, results);
    }
  }

  /// <summary>
  /// Retrieves all message envelopes within the specified time range.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetByTimeRangeAsync_WithEnvelopesWithoutCurrentHop_UsesMinValueTimestampAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetByTimeRangeAsync_WithNoHops_UsesMinValueTimestampAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByTimeRange_ShouldReturnMessagesInRangeAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByTimeRange_ShouldReturnEmptyWhenNoMatchesAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByTimeRange_ShouldReturnMessagesInChronologicalOrderAsync</tests>
  public Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset toTime, CancellationToken ct = default) {
    var results = _traces.Values
      .Where(e => {
        var timestamp = e.Hops?.FirstOrDefault(h => h.Type == HopType.Current)?.Timestamp ?? DateTimeOffset.MinValue;
        return timestamp >= from && timestamp <= toTime;
      })
      .OrderBy(e => e.Hops?.FirstOrDefault(h => h.Type == HopType.Current)?.Timestamp ?? DateTimeOffset.MinValue)
      .ToList();

    return Task.FromResult(results);
  }
}
