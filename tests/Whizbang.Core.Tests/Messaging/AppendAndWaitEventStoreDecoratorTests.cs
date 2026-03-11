using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="AppendAndWaitEventStoreDecorator"/>.
/// Verifies that AppendAndWaitAsync appends events and waits for perspective synchronization.
/// </summary>
[Category("EventStore")]
[Category("Sync")]
public class AppendAndWaitEventStoreDecoratorTests {
  private sealed record TestEvent(string Value) : IEvent;

  [Test]
  public async Task Constructor_WithNullInner_ThrowsArgumentNullExceptionAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = new AppendAndWaitEventStoreDecorator(
          null!,
          new FakePerspectiveSyncAwaiter());
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task Constructor_WithNullAwaiter_ThrowsArgumentNullExceptionAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = new AppendAndWaitEventStoreDecorator(
          new InMemoryEventStore(),
          null!);
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task AppendAndWaitAsync_AppendsEventToInnerStoreAsync() {
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    await decorator.AppendAndWaitAsync<TestEvent, FakePerspective>(
        streamId,
        message,
        TimeSpan.FromSeconds(5));

    // Verify event was appended
    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var e in inner.ReadAsync<TestEvent>(streamId, 0)) {
      events.Add(e);
    }

    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0].Payload.Value).IsEqualTo("test-data");
  }

  [Test]
  public async Task AppendAndWaitAsync_WaitsForPerspectiveSyncAsync() {
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter {
      ResultToReturn = new SyncResult(SyncOutcome.Synced, 1, TimeSpan.FromMilliseconds(50))
    };
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    var result = await decorator.AppendAndWaitAsync<TestEvent, FakePerspective>(
        streamId,
        message,
        TimeSpan.FromSeconds(5));

    // Verify awaiter was called
    await Assert.That(awaiter.WaitForStreamAsyncCalled).IsTrue();
    await Assert.That(awaiter.LastPerspectiveType).IsEqualTo(typeof(FakePerspective));
    await Assert.That(awaiter.LastStreamId).IsEqualTo(streamId);
    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.Synced);
  }

  [Test]
  public async Task AppendAndWaitAsync_UsesProvidedTimeoutAsync() {
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");
    var timeout = TimeSpan.FromSeconds(42);

    await decorator.AppendAndWaitAsync<TestEvent, FakePerspective>(
        streamId,
        message,
        timeout);

    await Assert.That(awaiter.LastTimeout).IsEqualTo(timeout);
  }

  [Test]
  public async Task AppendAndWaitAsync_WithNullTimeout_UsesDefaultTimeoutAsync() {
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    await decorator.AppendAndWaitAsync<TestEvent, FakePerspective>(
        streamId,
        message,
        timeout: null);

    // Default timeout should be 30 seconds
    await Assert.That(awaiter.LastTimeout).IsEqualTo(TimeSpan.FromSeconds(30));
  }

  [Test]
  public async Task AppendAndWaitAsync_WhenTimeoutOccurs_ReturnsTimedOutResultAsync() {
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter {
      ResultToReturn = new SyncResult(SyncOutcome.TimedOut, 0, TimeSpan.FromSeconds(5))
    };
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    var result = await decorator.AppendAndWaitAsync<TestEvent, FakePerspective>(
        streamId,
        message,
        TimeSpan.FromSeconds(5));

    await Assert.That(result.Outcome).IsEqualTo(SyncOutcome.TimedOut);
  }

  [Test]
  public async Task AppendAndWaitAsync_PassesCancellationTokenAsync() {
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");
    using var cts = new CancellationTokenSource();

    await decorator.AppendAndWaitAsync<TestEvent, FakePerspective>(
        streamId,
        message,
        TimeSpan.FromSeconds(5),
        cancellationToken: cts.Token);

    await Assert.That(awaiter.LastCancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task AppendAndWaitAsync_WhenCancelled_ThrowsOperationCanceledExceptionAsync() {
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter {
      ShouldThrowOnCancellation = true
    };
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAsync<OperationCanceledException>(async () => {
      await decorator.AppendAndWaitAsync<TestEvent, FakePerspective>(
          streamId,
          message,
          TimeSpan.FromSeconds(5),
          cancellationToken: cts.Token);
    });
  }

  [Test]
  public async Task AppendAsync_WithEnvelope_DelegatesToInnerAsync() {
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = messageId,
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    };

    await decorator.AppendAsync(streamId, envelope);

    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var e in inner.ReadAsync<TestEvent>(streamId, 0)) {
      events.Add(e);
    }

    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0].MessageId).IsEqualTo(messageId);
  }

  [Test]
  public async Task AppendAsync_WithMessage_DelegatesToInnerAsync() {
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    var message = new TestEvent("test-data");

    await decorator.AppendAsync(streamId, message);

    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var e in inner.ReadAsync<TestEvent>(streamId, 0)) {
      events.Add(e);
    }

    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0].Payload.Value).IsEqualTo("test-data");
  }

  [Test]
  public async Task ReadAsync_BySequence_DelegatesToInnerAsync() {
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    await inner.AppendAsync(streamId, new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    });

    var events = new List<MessageEnvelope<TestEvent>>();
    await foreach (var e in decorator.ReadAsync<TestEvent>(streamId, 0)) {
      events.Add(e);
    }

    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0].Payload.Value).IsEqualTo("test");
  }

  [Test]
  public async Task GetLastSequenceAsync_DelegatesToInnerAsync() {
    var inner = new InMemoryEventStore();
    var awaiter = new FakePerspectiveSyncAwaiter();
    var decorator = new AppendAndWaitEventStoreDecorator(inner, awaiter);

    var streamId = Guid.NewGuid();
    await inner.AppendAsync(streamId, new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent("test"),
      Hops = [new MessageHop { ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UtcNow }]
    });

    var lastSequence = await decorator.GetLastSequenceAsync(streamId);

    await Assert.That(lastSequence).IsGreaterThanOrEqualTo(0);
  }

  private sealed class FakePerspective { }

  private sealed class FakePerspectiveSyncAwaiter : IPerspectiveSyncAwaiter {
    public Guid AwaiterId { get; } = Guid.NewGuid();
    public bool WaitForStreamAsyncCalled { get; private set; }
    public Type? LastPerspectiveType { get; private set; }
    public Guid? LastStreamId { get; private set; }
    public TimeSpan LastTimeout { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }
    public SyncResult ResultToReturn { get; set; } = new(SyncOutcome.Synced, 1, TimeSpan.FromMilliseconds(10));
    public bool ShouldThrowOnCancellation { get; set; }

    public Task<SyncResult> WaitAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) {
      throw new NotImplementedException("Not used by AppendAndWaitAsync");
    }

    public Task<bool> IsCaughtUpAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) {
      throw new NotImplementedException("Not used by AppendAndWaitAsync");
    }

    public Task<SyncResult> WaitForStreamAsync(
        Type perspectiveType,
        Guid streamId,
        Type[]? eventTypes,
        TimeSpan timeout,
        Guid? eventIdToAwait = null,
        CancellationToken ct = default) {
      if (ShouldThrowOnCancellation && ct.IsCancellationRequested) {
        throw new OperationCanceledException(ct);
      }

      WaitForStreamAsyncCalled = true;
      LastPerspectiveType = perspectiveType;
      LastStreamId = streamId;
      LastTimeout = timeout;
      LastCancellationToken = ct;

      return Task.FromResult(ResultToReturn);
    }
  }
}
