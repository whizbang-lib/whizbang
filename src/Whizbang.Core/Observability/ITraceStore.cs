using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Observability;

/// <summary>
/// Store for message traces enabling observability queries.
/// Stores complete message envelopes with all hops, metadata, and policy decisions.
/// </summary>
/// <docs>core-concepts/observability</docs>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs</tests>
/// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs</tests>
public interface ITraceStore {
  /// <summary>
  /// Stores a message envelope trace.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_StoreAndRetrieve_ShouldStoreAndRetrieveEnvelopeAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_ConcurrentStores_ShouldHandleConcurrencyAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:StoreAsync_WithNullEnvelope_ThrowsArgumentNullExceptionAsync</tests>
  Task StoreAsync(IMessageEnvelope envelope, CancellationToken ct = default);

  /// <summary>
  /// Retrieves a message envelope by MessageId.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_StoreAndRetrieve_ShouldStoreAndRetrieveEnvelopeAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByMessageId_ShouldReturnNullForNonExistentTraceAsync</tests>
  Task<IMessageEnvelope?> GetByMessageIdAsync(MessageId messageId, CancellationToken ct = default);

  /// <summary>
  /// Retrieves all message envelopes with the same CorrelationId.
  /// Returns messages in chronological order by first hop timestamp.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByCorrelation_ShouldReturnAllMessagesWithSameCorrelationIdAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByCorrelation_ShouldReturnEmptyListWhenNoMatchesAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetByCorrelationAsync_WithNullCorrelationIdsInStore_FiltersThemOutAsync</tests>
  Task<List<IMessageEnvelope>> GetByCorrelationAsync(CorrelationId correlationId, CancellationToken ct = default);

  /// <summary>
  /// Retrieves the complete causal chain for a message.
  /// Includes the message itself, all parent messages (via CausationId), and all child messages.
  /// Returns messages in chronological order.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetCausalChain_ShouldReturnMessageAndParentsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetCausalChain_ShouldReturnJustMessageWhenNoParentsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetCausalChain_ShouldReturnEmptyWhenMessageNotFoundAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithCircularReference_ProtectsAgainstInfiniteLoopAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithMissingParent_StopsWalkingUpChainAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithChildren_IncludesChildMessagesAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithMultiGenerationChildren_IncludesAllDescendantsAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithEmptyCausationId_StopsWalkingAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_SortsResultsByTimestampAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetCausalChainAsync_WithCircularReferenceInChildrenTree_ProtectsAgainstInfiniteLoopAsync</tests>
  Task<List<IMessageEnvelope>> GetCausalChainAsync(MessageId messageId, CancellationToken ct = default);

  /// <summary>
  /// Retrieves all message envelopes within a time range.
  /// Time range is based on first hop timestamp.
  /// Returns messages in chronological order.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByTimeRange_ShouldReturnMessagesInRangeAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByTimeRange_ShouldReturnEmptyWhenNoMatchesAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/TraceStoreContractTests.cs:TraceStore_GetByTimeRange_ShouldReturnMessagesInChronologicalOrderAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetByTimeRangeAsync_WithEnvelopesWithoutCurrentHop_UsesMinValueTimestampAsync</tests>
  /// <tests>tests/Whizbang.Observability.Tests/TraceStore/InMemoryTraceStoreTests.cs:GetByTimeRangeAsync_WithNoHops_UsesMinValueTimestampAsync</tests>
  Task<List<IMessageEnvelope>> GetByTimeRangeAsync(DateTimeOffset from, DateTimeOffset toTime, CancellationToken ct = default);
}
