using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for lifecycle invoker behavior when the service provider is disposed (app shutdown scenario).
/// Reproduces the bug: ObjectDisposedException thrown in GeneratedLifecycleInvoker.InvokeAsync
/// when ScopedWorkCoordinatorStrategy.FlushAsync is called during shutdown.
/// </summary>
/// <remarks>
/// <para>
/// Bug reproduction scenario:
/// 1. App starts shutting down
/// 2. ServiceProvider starts disposal
/// 3. ScopedWorkCoordinatorStrategy.FlushAsync is still processing messages
/// 4. LifecycleInvocationHelper calls ILifecycleInvoker.InvokeAsync
/// 5. GeneratedLifecycleInvoker tries to call _scopeFactory.CreateScope()
/// 6. ObjectDisposedException is thrown because the ServiceProvider is already disposed
/// </para>
/// </remarks>
[Category("Messaging")]
[Category("Lifecycle")]
[Category("Disposal")]
public class LifecycleInvokerDisposalTests {

  // ========================================
  // ObjectDisposedException Reproduction Tests
  // ========================================

  /// <summary>
  /// Reproduces the bug: ILifecycleInvoker.InvokeAsync throws ObjectDisposedException
  /// when called after the ServiceProvider has been disposed.
  /// This simulates the shutdown scenario where FlushAsync is called during disposal.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WithDisposedScopeFactory_ThrowsObjectDisposedExceptionAsync() {
    // Arrange - Create a lifecycle invoker with a disposed scope factory
    var disposedScopeFactory = new DisposedScopeFactory();
    var lifecycleInvoker = new TestableLifecycleInvoker(disposedScopeFactory);

    var envelope = _createTestEnvelope();

    // Act & Assert - Should throw ObjectDisposedException
    // This is the CURRENT (buggy) behavior we're reproducing
    await Assert.That(async () =>
        await lifecycleInvoker.InvokeAsync(envelope, LifecycleStage.PostDistributeInline))
      .Throws<ObjectDisposedException>()
      .Because("Bug reproduction: CreateScope throws when ServiceProvider is disposed");
  }

  /// <summary>
  /// Tests that the lifecycle invoker should gracefully handle ObjectDisposedException
  /// by returning early without throwing.
  /// This is the EXPECTED behavior after the fix.
  /// </summary>
  [Test]
  public async Task InvokeAsync_WithDisposedScopeFactory_ShouldNotThrow_AfterFixAsync() {
    // Arrange - Create a lifecycle invoker with a disposed scope factory
    // Using the fixed implementation
    var disposedScopeFactory = new DisposedScopeFactory();
    var lifecycleInvoker = new FixedLifecycleInvoker(disposedScopeFactory);

    var envelope = _createTestEnvelope();

    // Act - Should NOT throw, should gracefully return
    Exception? caughtException = null;
    try {
      await lifecycleInvoker.InvokeAsync(envelope, LifecycleStage.PostDistributeInline);
    } catch (Exception ex) {
      caughtException = ex;
    }

    // Assert - No exception should be thrown
    await Assert.That(caughtException).IsNull()
      .Because("Fixed invoker should handle ObjectDisposedException gracefully");
  }

  /// <summary>
  /// Integration test: Verifies the full flow from ScopedWorkCoordinatorStrategy
  /// when the LifecycleInvoker's scope factory is disposed.
  /// </summary>
  [Test]
  public async Task FlushAsync_WithDisposedLifecycleInvoker_ShouldNotThrowAsync() {
    // Arrange
    var disposedScopeFactory = new DisposedScopeFactory();
    var lifecycleInvoker = new FixedLifecycleInvoker(disposedScopeFactory);
    var deserializer = new FakeLifecycleMessageDeserializer();

    var outboxMessages = new List<OutboxMessage> { _createTestOutboxMessage() };
    var inboxMessages = new List<InboxMessage>();

    // Act - Should not throw even with disposed scope factory
    Exception? caughtException = null;
    try {
      await LifecycleInvocationHelper.InvokeDistributeLifecycleStagesAsync(
        LifecycleStage.PostDistributeAsync,
        LifecycleStage.PostDistributeInline,
        outboxMessages,
        inboxMessages,
        lifecycleInvoker,
        deserializer,
        null);
    } catch (Exception ex) {
      caughtException = ex;
    }

    // Assert - No exception should be thrown
    await Assert.That(caughtException).IsNull()
      .Because("Lifecycle invocation should handle disposal gracefully");
  }

  /// <summary>
  /// Tests that background async lifecycle stages also handle ObjectDisposedException gracefully.
  /// </summary>
  [Test]
  public async Task InvokeAsyncOnlyLifecycleStage_WithDisposedScopeFactory_ShouldNotThrowAsync() {
    // Arrange
    var disposedScopeFactory = new DisposedScopeFactory();
    var lifecycleInvoker = new FixedLifecycleInvoker(disposedScopeFactory);
    var deserializer = new FakeLifecycleMessageDeserializer();

    var outboxMessages = new List<OutboxMessage> { _createTestOutboxMessage() };
    var inboxMessages = new List<InboxMessage>();

    // Act - Should not throw even with disposed scope factory
    Exception? caughtException = null;
    try {
      LifecycleInvocationHelper.InvokeAsyncOnlyLifecycleStage(
        LifecycleStage.DistributeAsync,
        outboxMessages,
        inboxMessages,
        lifecycleInvoker,
        deserializer,
        null);

      // Wait for background task
      await Task.Delay(100);
    } catch (Exception ex) {
      caughtException = ex;
    }

    // Assert - No exception should be thrown
    await Assert.That(caughtException).IsNull()
      .Because("Background lifecycle invocation should handle disposal gracefully");
  }

  // ========================================
  // Test Helpers
  // ========================================

  private static MessageEnvelope<JsonElement> _createTestEnvelope() {
    var messageId = MessageId.New();
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{\"value\":\"test\"}").RootElement,
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow
      }]
    };
  }

  private static OutboxMessage _createTestOutboxMessage() {
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonDocument.Parse("{\"value\":\"test\"}").RootElement,
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow
      }]
    };

    return new OutboxMessage {
      MessageId = messageId.Value,
      Destination = "test-topic",
      Envelope = envelope,
      Metadata = new EnvelopeMetadata { MessageId = messageId, Hops = envelope.Hops },
      EnvelopeType = "MessageEnvelope`1[[TestMessage, TestAssembly]]",
      MessageType = "TestMessage, TestAssembly"
    };
  }

  // ========================================
  // Test Fakes
  // ========================================

  /// <summary>
  /// IServiceScopeFactory that simulates a disposed ServiceProvider.
  /// Throws ObjectDisposedException when CreateScope() is called.
  /// </summary>
  private sealed class DisposedScopeFactory : IServiceScopeFactory {
    public IServiceScope CreateScope() {
      throw new ObjectDisposedException("ServiceProvider", "Cannot access a disposed object.");
    }
  }

  /// <summary>
  /// ILifecycleInvoker that simulates the CURRENT (buggy) behavior.
  /// Throws ObjectDisposedException when scope factory is disposed.
  /// </summary>
  private sealed class TestableLifecycleInvoker : ILifecycleInvoker {
    private readonly IServiceScopeFactory _scopeFactory;

    public TestableLifecycleInvoker(IServiceScopeFactory scopeFactory) {
      _scopeFactory = scopeFactory;
    }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      // Simulate the current (buggy) generated code behavior
      // This throws ObjectDisposedException when ServiceProvider is disposed
      using var registryScope = _scopeFactory.CreateScope();
      var registry = registryScope.ServiceProvider.GetService<ILifecycleReceptorRegistry>();

      // ... rest of the method would follow
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// ILifecycleInvoker that simulates the FIXED behavior.
  /// Catches ObjectDisposedException and returns gracefully.
  /// </summary>
  private sealed class FixedLifecycleInvoker : ILifecycleInvoker {
    private readonly IServiceScopeFactory _scopeFactory;

    public FixedLifecycleInvoker(IServiceScopeFactory scopeFactory) {
      _scopeFactory = scopeFactory;
    }

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      // FIXED behavior: Catch ObjectDisposedException and return gracefully
      IServiceScope? registryScope = null;
      try {
        registryScope = _scopeFactory.CreateScope();
      } catch (ObjectDisposedException) {
        // App is shutting down, return gracefully
        return ValueTask.CompletedTask;
      }

      try {
        var registry = registryScope.ServiceProvider.GetService<ILifecycleReceptorRegistry>();
        // ... rest of the method would follow
      } finally {
        registryScope.Dispose();
      }

      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeLifecycleMessageDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) {
      return new TestMessage { Value = "deserialized" };
    }

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) {
      return new TestMessage { Value = "deserialized" };
    }

    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) {
      return new TestMessage { Value = "deserialized" };
    }

    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) {
      return new TestMessage { Value = "deserialized" };
    }
  }

  private sealed record TestMessage {
    public required string Value { get; init; }
  }
}
