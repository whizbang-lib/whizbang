using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Messaging;

/// <summary>
/// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs:WaitForResponseAsync_WhenRequestNotFound_ShouldReturnNullAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs:CleanupExpiredAsync_WithExpiredRecords_ShouldRemoveThemAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs:CleanupExpiredAsync_WithNonExpiredRecords_ShouldKeepThemAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ThenSaveRequest_ShouldGetResponseAsync</tests>
/// In-memory implementation of IRequestResponseStore for testing and single-process scenarios.
/// Thread-safe using ConcurrentDictionary and TaskCompletionSource.
/// NOT suitable for production use across multiple processes.
/// </summary>
public class InMemoryRequestResponseStore : IRequestResponseStore {
  private readonly ConcurrentDictionary<CorrelationId, RequestRecord> _requests = new();

  /// <inheritdoc />
  /// <tests>src/Whizbang.Testing/Contracts/RequestResponseStoreContractTests.cs</tests>
  public Task SaveRequestAsync(CorrelationId correlationId, MessageId requestId, TimeSpan timeout, CancellationToken cancellationToken = default) {

    var tcs = new TaskCompletionSource<IMessageEnvelope>();
    var record = new RequestRecord(
      RequestId: requestId,
      CompletionSource: tcs,
      ExpiresAt: DateTimeOffset.UtcNow + timeout
    );

    _requests.TryAdd(correlationId, record);

    // Setup timeout
    _ = Task.Run(async () => {
      await Task.Delay(timeout, cancellationToken);
      if (_requests.TryRemove(correlationId, out var expiredRecord)) {
        expiredRecord.CompletionSource.TrySetResult(null!);
      }
    }, cancellationToken);

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs:WaitForResponseAsync_WhenRequestNotFound_ShouldReturnNullAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreCancellationTests.cs:WaitForResponseAsync_OneWaiterCanceled_TheOtherStillReceivesTheResponseAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/RequestResponseStoreContractTests.cs</tests>
  public async Task<IMessageEnvelope?> WaitForResponseAsync(CorrelationId correlationId, CancellationToken cancellationToken = default) {

    if (!_requests.TryGetValue(correlationId, out var record)) {
      // Request not found, might have already completed or timed out
      return null;
    }

    try {
      // The token governs this wait only. Canceling the shared completion source instead would cancel
      // every other waiter on the correlation and leave a response that arrives afterwards with nowhere
      // to go.
      return await record.CompletionSource.Task.WaitAsync(cancellationToken); // null if it timed out
    } catch (OperationCanceledException) {
      return null;
    }
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreCancellationTests.cs:WaitForResponseAsyncGeneric_OneWaiterCanceled_TheOtherStillReceivesTheResponseAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/RequestResponseStoreContractTests.cs</tests>
  public async Task<MessageEnvelope<TMessage>?> WaitForResponseAsync<TMessage>(CorrelationId correlationId, CancellationToken cancellationToken = default)
    => await WaitForResponseAsync(correlationId, cancellationToken) as MessageEnvelope<TMessage>;

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs:SaveResponseAsync_BeforeSaveRequest_ThenSaveRequest_ShouldGetResponseAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/RequestResponseStoreContractTests.cs</tests>
  public Task SaveResponseAsync(CorrelationId correlationId, IMessageEnvelope response, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(response);

    if (_requests.TryRemove(correlationId, out var record)) {
      record.CompletionSource.TrySetResult(response);
    } else {
      // Request not found - might be a race condition where response arrives before request is saved
      // Store the response so it can be retrieved when request is saved
      var tcs = new TaskCompletionSource<IMessageEnvelope>();
      tcs.SetResult(response);
      var newRecord = new RequestRecord(
        RequestId: MessageId.New(), // Placeholder
        CompletionSource: tcs,
        ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1) // Short expiry
      );
      _requests.TryAdd(correlationId, newRecord);
    }

    return Task.CompletedTask;
  }

  /// <inheritdoc />
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs:CleanupExpiredAsync_WithExpiredRecords_ShouldRemoveThemAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/InMemoryRequestResponseStoreTests.cs:CleanupExpiredAsync_WithNonExpiredRecords_ShouldKeepThemAsync</tests>
  /// <tests>src/Whizbang.Testing/Contracts/RequestResponseStoreContractTests.cs</tests>
  public Task CleanupExpiredAsync(CancellationToken cancellationToken = default) {
    var now = DateTimeOffset.UtcNow;
    var expiredKeys = new System.Collections.Generic.List<CorrelationId>();

    foreach (var kvp in _requests) {
      if (kvp.Value.ExpiresAt < now) {
        expiredKeys.Add(kvp.Key);
      }
    }

    foreach (var key in expiredKeys) {
      if (_requests.TryRemove(key, out var record)) {
        record.CompletionSource.TrySetResult(null!);
      }
    }

    return Task.CompletedTask;
  }

  private sealed record RequestRecord(
    MessageId RequestId,
    TaskCompletionSource<IMessageEnvelope> CompletionSource,
    DateTimeOffset ExpiresAt
  );
}
