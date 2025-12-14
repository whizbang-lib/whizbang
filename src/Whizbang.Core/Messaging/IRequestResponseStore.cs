using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Stores request/response correlations for transports without native RPC support.
/// Uses CorrelationId (existing value object) to match requests with responses.
/// Enables request/response pattern on Kafka and Event Hubs.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/RequestResponseStoreContractTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs</tests>
public interface IRequestResponseStore {
  /// <summary>
  /// Saves a request and sets up tracking for the expected response.
  /// </summary>
  /// <param name="correlationId">The correlation ID linking request and response</param>
  /// <param name="requestId">The request message ID</param>
  /// <param name="timeout">How long to wait for a response before timing out</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the request is saved</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RequestResponseStoreContractTests.cs:SaveRequestAsync_ShouldStoreRequestAsync</tests>
  Task SaveRequestAsync(CorrelationId correlationId, MessageId requestId, TimeSpan timeout, CancellationToken cancellationToken = default);

  /// <summary>
  /// Waits for a response to arrive for the given correlation ID.
  /// Returns null if the request times out.
  /// </summary>
  /// <param name="correlationId">The correlation ID to wait for</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The response envelope, or null if timed out</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs:WaitForResponseAsync_WhenRequestNotFound_ShouldReturnNullAsync</tests>
  Task<IMessageEnvelope?> WaitForResponseAsync(CorrelationId correlationId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Waits for a response to arrive for the given correlation ID with strong typing.
  /// Returns null if the request times out.
  /// This generic version provides type-safe deserialization for AOT compatibility.
  /// </summary>
  /// <typeparam name="TMessage">The response message type to deserialize</typeparam>
  /// <param name="correlationId">The correlation ID to wait for</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>The strongly-typed response envelope, or null if timed out</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RequestResponseStoreContractTests.cs:WaitForResponseAsync_WithoutResponse_ShouldTimeoutAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RequestResponseStoreContractTests.cs:WaitForResponseAsync_WithCancellation_ShouldRespectCancellationAsync</tests>
  Task<MessageEnvelope<TMessage>?> WaitForResponseAsync<TMessage>(CorrelationId correlationId, CancellationToken cancellationToken = default);

  /// <summary>
  /// Saves a response and notifies any waiting request.
  /// </summary>
  /// <param name="correlationId">The correlation ID linking request and response</param>
  /// <param name="response">The response message envelope</param>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when the response is saved</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RequestResponseStoreContractTests.cs:SaveResponseAsync_ShouldCompleteWaitingRequestAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RequestResponseStoreContractTests.cs:SaveResponseAsync_WithNullResponse_ShouldThrowAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RequestResponseStoreContractTests.cs:SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ThenSaveRequest_ShouldGetResponseAsync</tests>
  Task SaveResponseAsync(CorrelationId correlationId, IMessageEnvelope response, CancellationToken cancellationToken = default);

  /// <summary>
  /// Cleans up expired/timed-out request/response records.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token</param>
  /// <returns>Task that completes when cleanup is finished</returns>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/RequestResponseStoreContractTests.cs:CleanupExpiredAsync_ShouldNotThrowAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs:CleanupExpiredAsync_WithExpiredRecords_ShouldRemoveThemAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs:CleanupExpiredAsync_WithNonExpiredRecords_ShouldKeepThemAsync</tests>
  Task CleanupExpiredAsync(CancellationToken cancellationToken = default);
}
