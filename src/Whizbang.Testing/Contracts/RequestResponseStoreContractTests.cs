using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Testing.Contracts;

/// <summary>
/// Test response type for request/response store tests.
/// </summary>
public record TestResponse([StreamId] string Message) : IEvent;

/// <summary>
/// Contract tests for IRequestResponseStore interface.
/// All implementations of IRequestResponseStore must pass these tests.
/// </summary>
[Category("Messaging")]
public abstract class RequestResponseStoreContractTests {
  /// <summary>
  /// Derived test classes must provide a factory method to create an IRequestResponseStore instance.
  /// </summary>
  protected abstract Task<IRequestResponseStore> CreateStoreAsync();

  [Test]
  public async Task SaveRequestAsync_ShouldStoreRequestAsync() {
    // Arrange
    var store = await CreateStoreAsync();
    var correlationId = CorrelationId.New();
    var requestId = MessageId.New();
    var timeout = TimeSpan.FromSeconds(30);

    // Act & Assert - Should not throw
    await store.SaveRequestAsync(correlationId, requestId, timeout);
  }

  [Test]
  public async Task WaitForResponseAsync_WithoutResponse_ShouldTimeoutAsync() {
    // Arrange
    var store = await CreateStoreAsync();
    var correlationId = CorrelationId.New();
    var requestId = MessageId.New();
    await store.SaveRequestAsync(correlationId, requestId, TimeSpan.FromMilliseconds(100));

    // Act
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
    var response = await store.WaitForResponseAsync<TestResponse>(correlationId, cts.Token);

    // Assert - Should return null after timeout
    await Assert.That(response).IsNull();
  }

  [Test]
  public async Task SaveResponseAsync_ShouldCompleteWaitingRequestAsync() {
    // Arrange
    var store = await CreateStoreAsync();
    var correlationId = CorrelationId.New();
    var requestId = MessageId.New();
    var responseEnvelope = _createTestEnvelope();
    await store.SaveRequestAsync(correlationId, requestId, TimeSpan.FromSeconds(30));

    // Act - begin waiting, THEN complete the wait.
    //
    // This used to fire the save from a detached Task.Run after a 100 ms sleep and race it against
    // a 5-second deadline. The sleep was standing in for "the waiter has registered by now", and
    // the detached task can simply be starved past the deadline on a loaded machine — which is how
    // it failed in CI, as a bare "expected not null". Starting the waiter and then saving needs no
    // sleep and cannot be starved: the save is awaited on this thread.
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));   // hang guard only
    var waiter = store.WaitForResponseAsync<TestResponse>(correlationId, cts.Token);
    await store.SaveResponseAsync(correlationId, responseEnvelope);
    var response = await waiter;

    // Assert
    await Assert.That(response).IsNotNull();
    await Assert.That(response!.MessageId).IsEqualTo(responseEnvelope.MessageId);
  }

  [Test]
  public async Task SaveResponseAsync_WithNullResponse_ShouldThrowAsync() {
    // Arrange
    var store = await CreateStoreAsync();
    var correlationId = CorrelationId.New();

    // Act & Assert
    await Assert.That(() => store.SaveResponseAsync(correlationId, null!))
      .ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task CleanupExpiredAsync_ShouldNotThrowAsync() {
    // Arrange
    var store = await CreateStoreAsync();

    // Act & Assert - Should not throw
    await store.CleanupExpiredAsync();
  }

  [Test]
  public async Task WaitForResponseAsync_WithCancellation_ShouldRespectCancellationAsync() {
    // Arrange
    var store = await CreateStoreAsync();
    var correlationId = CorrelationId.New();
    var requestId = MessageId.New();
    await store.SaveRequestAsync(correlationId, requestId, TimeSpan.FromMinutes(10));

    // Act
    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    var response = await store.WaitForResponseAsync<TestResponse>(correlationId, cts.Token);

    // Assert - Should return null due to cancellation
    await Assert.That(response).IsNull();
  }

  [Test]
  public async Task SaveResponseAsync_BeforeSaveRequest_ShouldNotCauseProblemAsync() {
    // Arrange
    var store = await CreateStoreAsync();
    var correlationId = CorrelationId.New();
    var responseEnvelope = _createTestEnvelope();

    // Act - Save response before request (race condition scenario)
    await store.SaveResponseAsync(correlationId, responseEnvelope);

    // Save request after response
    var requestId = MessageId.New();
    await store.SaveRequestAsync(correlationId, requestId, TimeSpan.FromSeconds(30));

    // Wait for response
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    var response = await store.WaitForResponseAsync<TestResponse>(correlationId, cts.Token);

    // Assert - Should get the response
    await Assert.That(response).IsNotNull();
  }

  /// <summary>
  /// Helper method to create a test message envelope.
  /// </summary>
  private static MessageEnvelope<TestResponse> _createTestEnvelope() {
    return new MessageEnvelope<TestResponse> {
      MessageId = MessageId.New(),
      Payload = new TestResponse("test response"),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }
}
