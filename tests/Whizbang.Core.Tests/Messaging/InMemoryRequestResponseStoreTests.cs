using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for InMemoryRequestResponseStore implementation.
/// Inherits all contract tests from RequestResponseStoreContractTests.
/// </summary>
[InheritsTests]
public class InMemoryRequestResponseStoreTests : RequestResponseStoreContractTests {
  protected override Task<IRequestResponseStore> CreateStoreAsync() {
    return Task.FromResult<IRequestResponseStore>(new InMemoryRequestResponseStore());
  }

  [Test]
  public async Task WaitForResponseAsync_WhenRequestNotFound_ShouldReturnNullAsync() {
    // Arrange
    var store = new InMemoryRequestResponseStore();
    var correlationId = CorrelationId.New();

    // Act - try to wait for a request that was never saved
    var result = await store.WaitForResponseAsync(correlationId, CancellationToken.None);

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task CleanupExpiredAsync_WithExpiredRecords_ShouldRemoveThemAsync() {
    // Arrange
    var store = new InMemoryRequestResponseStore();
    var correlationId1 = CorrelationId.New();
    var correlationId2 = CorrelationId.New();
    var requestId1 = MessageId.New();
    var requestId2 = MessageId.New();

    // Save two requests with very short timeout (1ms) to ensure they expire
    await store.SaveRequestAsync(correlationId1, requestId1, TimeSpan.FromMilliseconds(1), CancellationToken.None);
    await store.SaveRequestAsync(correlationId2, requestId2, TimeSpan.FromMilliseconds(1), CancellationToken.None);

    // Wait for them to expire
    await Task.Delay(100);

    // Act - cleanup expired records
    await store.CleanupExpiredAsync(CancellationToken.None);

    // Assert - both requests should now return null (cleaned up)
    var result1 = await store.WaitForResponseAsync(correlationId1, CancellationToken.None);
    var result2 = await store.WaitForResponseAsync(correlationId2, CancellationToken.None);

    await Assert.That(result1).IsNull();
    await Assert.That(result2).IsNull();
  }

  [Test]
  public async Task CleanupExpiredAsync_WithNonExpiredRecords_ShouldKeepThemAsync() {
    // Arrange
    var store = new InMemoryRequestResponseStore();
    var expiredCorrelationId = CorrelationId.New();
    var nonExpiredCorrelationId = CorrelationId.New();
    var expiredRequestId = MessageId.New();
    var nonExpiredRequestId = MessageId.New();

    // Save one expired request (1ms timeout)
    await store.SaveRequestAsync(expiredCorrelationId, expiredRequestId, TimeSpan.FromMilliseconds(1), CancellationToken.None);

    // Save one non-expired request (10 seconds timeout)
    await store.SaveRequestAsync(nonExpiredCorrelationId, nonExpiredRequestId, TimeSpan.FromSeconds(10), CancellationToken.None);

    // Wait for the expired one to expire
    await Task.Delay(100);

    // Act - cleanup (should only remove the expired record)
    await store.CleanupExpiredAsync(CancellationToken.None);

    // Assert - expired record should return null (cleaned up)
    var expiredResult = await store.WaitForResponseAsync(expiredCorrelationId, CancellationToken.None);
    await Assert.That(expiredResult).IsNull();

    // Now test the non-expired one - start waiting for response (will block)
    var response = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "response",
      Hops = []
    };

    // Start waiting and saving response in parallel
    var waitTask = store.WaitForResponseAsync(nonExpiredCorrelationId, CancellationToken.None);
    await Task.Delay(10); // Give wait a chance to start
    await store.SaveResponseAsync(nonExpiredCorrelationId, response, CancellationToken.None);

    // Wait for response - should still work (record was not cleaned up)
    var nonExpiredResult = await waitTask;
    await Assert.That(nonExpiredResult).IsNotNull();
    await Assert.That(((MessageEnvelope<string>)nonExpiredResult!).Payload).IsEqualTo("response");
  }

  [Test]
  public async Task SaveResponseAsync_BeforeSaveRequest_ThenSaveRequest_ShouldGetResponseAsync() {
    // Arrange
    var store = new InMemoryRequestResponseStore();
    var correlationId = CorrelationId.New();
    var requestId = MessageId.New();

    var response = new MessageEnvelope<string> {
      MessageId = MessageId.New(),
      Payload = "early response",
      Hops = []
    };

    // Act - save response BEFORE saving request (race condition scenario)
    await store.SaveResponseAsync(correlationId, response, CancellationToken.None);

    // Now save the request
    await store.SaveRequestAsync(correlationId, requestId, TimeSpan.FromSeconds(5), CancellationToken.None);

    // Wait for response
    var result = await store.WaitForResponseAsync(correlationId, CancellationToken.None);

    // Assert - should get the response
    await Assert.That(result).IsNotNull();
    await Assert.That(((MessageEnvelope<string>)result!).Payload).IsEqualTo("early response");
  }
}
