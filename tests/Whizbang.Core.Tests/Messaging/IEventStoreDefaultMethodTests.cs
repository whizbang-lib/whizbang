#pragma warning disable CA1707

using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for IEventStore default interface method implementations.
/// The AppendAndWaitAsync methods have default implementations that throw
/// NotSupportedException when the decorator is not registered.
/// </summary>
[Category("EventStore")]
public class IEventStoreDefaultMethodTests {

  // ========================================
  // AppendAndWaitAsync<TMessage, TPerspective> Default Implementation
  // ========================================

  [Test]
  public async Task AppendAndWaitAsync_WithPerspective_DefaultImplementation_ThrowsNotSupportedExceptionAsync() {
    // Arrange
    IEventStore eventStore = new MinimalEventStore();
    var streamId = Guid.NewGuid();
    var message = new TestEvent { StreamId = streamId, Payload = "test" };

    // Act & Assert
    await Assert.That(async () =>
        await eventStore.AppendAndWaitAsync<TestEvent, TestPerspective>(streamId, message))
      .ThrowsExactly<NotSupportedException>();
  }

  [Test]
  public async Task AppendAndWaitAsync_WithPerspective_DefaultImplementation_HasDescriptiveMessageAsync() {
    // Arrange
    IEventStore eventStore = new MinimalEventStore();
    var streamId = Guid.NewGuid();
    var message = new TestEvent { StreamId = streamId, Payload = "test" };

    // Act
    NotSupportedException? caught = null;
    try {
      await eventStore.AppendAndWaitAsync<TestEvent, TestPerspective>(streamId, message);
    } catch (NotSupportedException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("AppendAndWaitEventStoreDecorator");
  }

  // ========================================
  // AppendAndWaitAsync<TMessage> (all perspectives) Default Implementation
  // ========================================

  [Test]
  public async Task AppendAndWaitAsync_AllPerspectives_DefaultImplementation_ThrowsNotSupportedExceptionAsync() {
    // Arrange
    IEventStore eventStore = new MinimalEventStore();
    var streamId = Guid.NewGuid();
    var message = new TestEvent { StreamId = streamId, Payload = "test" };

    // Act & Assert
    await Assert.That(async () =>
        await eventStore.AppendAndWaitAsync(streamId, message))
      .ThrowsExactly<NotSupportedException>();
  }

  [Test]
  public async Task AppendAndWaitAsync_AllPerspectives_DefaultImplementation_HasDescriptiveMessageAsync() {
    // Arrange
    IEventStore eventStore = new MinimalEventStore();
    var streamId = Guid.NewGuid();
    var message = new TestEvent { StreamId = streamId, Payload = "test" };

    // Act
    NotSupportedException? caught = null;
    try {
      await eventStore.AppendAndWaitAsync(streamId, message);
    } catch (NotSupportedException ex) {
      caught = ex;
    }

    // Assert
    await Assert.That(caught).IsNotNull();
    await Assert.That(caught!.Message).Contains("IEventCompletionAwaiter");
  }

  [Test]
  public async Task AppendAndWaitAsync_WithPerspective_WithOptionalParams_StillThrowsAsync() {
    // Arrange
    IEventStore eventStore = new MinimalEventStore();
    var streamId = Guid.NewGuid();
    var message = new TestEvent { StreamId = streamId, Payload = "test" };

    // Act & Assert - even with all optional parameters, should still throw
    await Assert.That(async () =>
        await eventStore.AppendAndWaitAsync<TestEvent, TestPerspective>(
          streamId,
          message,
          timeout: TimeSpan.FromSeconds(5),
          onWaiting: _ => { },
          onDecisionMade: _ => { }))
      .ThrowsExactly<NotSupportedException>();
  }

  [Test]
  public async Task AppendAndWaitAsync_AllPerspectives_WithOptionalParams_StillThrowsAsync() {
    // Arrange
    IEventStore eventStore = new MinimalEventStore();
    var streamId = Guid.NewGuid();
    var message = new TestEvent { StreamId = streamId, Payload = "test" };

    // Act & Assert - even with all optional parameters, should still throw
    await Assert.That(async () =>
        await eventStore.AppendAndWaitAsync(
          streamId,
          message,
          timeout: TimeSpan.FromSeconds(5),
          onWaiting: _ => { },
          onDecisionMade: _ => { }))
      .ThrowsExactly<NotSupportedException>();
  }

  // ========================================
  // Test Support Types
  // ========================================

  /// <summary>
  /// Minimal IEventStore implementation that only implements required members.
  /// Default interface methods (AppendAndWaitAsync) are NOT overridden.
  /// </summary>
  private sealed class MinimalEventStore : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope,
        CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message,
        CancellationToken cancellationToken = default) where TMessage : notnull =>
      Task.CompletedTask;

    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
        Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }

    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(
        Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
        Guid streamId, Guid? afterEventId, Guid upToEventId,
        CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes,
        CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(-1L);
  }

  private sealed record TestEvent : IEvent {
    public Guid StreamId { get; init; }
    public string Payload { get; init; } = string.Empty;
  }

  private sealed class TestPerspective { }
}
