using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// Tests for <see cref="SecurityContextEventStoreDecorator"/>.
/// Verifies security context propagation from ambient scope to event envelopes.
/// </summary>
[Category("Messaging")]
[Category("Security")]
public sealed class SecurityContextEventStoreDecoratorTests {
  private sealed record TestEvent(string Data);

  [Test]
  public async Task Constructor_WithNullInner_ThrowsArgumentNullExceptionAsync() {
    await Assert.ThrowsAsync<ArgumentNullException>(async () => {
      _ = new SecurityContextEventStoreDecorator(null!);
      await Task.CompletedTask;
    });
  }

  [Test]
  public async Task AppendAsync_WithMessage_WithAmbientSecurityContext_PropagatesContextAsync() {
    // Arrange
    var capturingStore = new CapturingEventStore();
    var decorator = new SecurityContextEventStoreDecorator(capturingStore);
    var streamId = Guid.NewGuid();
    var message = new TestEvent("test");

    var scope = new PerspectiveScope { UserId = "user-123", TenantId = "tenant-456" };
    var extraction = new SecurityExtraction {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var immutableContext = new ImmutableScopeContext(extraction, shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = immutableContext;

    try {
      // Act
      await decorator.AppendAsync(streamId, message);

      // Assert
      await Assert.That(capturingStore.CapturedEnvelope).IsNotNull();
      var hop = capturingStore.CapturedEnvelope!.Hops[0];
      await Assert.That(hop.Scope).IsNotNull();
      var scopeContext = capturingStore.CapturedEnvelope.GetCurrentScope();
      await Assert.That(scopeContext?.Scope?.UserId).IsEqualTo("user-123");
      await Assert.That(scopeContext?.Scope?.TenantId).IsEqualTo("tenant-456");
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task AppendAsync_WithMessage_WithoutAmbientContext_CreatesEnvelopeWithNullScopeAsync() {
    // Arrange
    var capturingStore = new CapturingEventStore();
    var decorator = new SecurityContextEventStoreDecorator(capturingStore);
    var streamId = Guid.NewGuid();
    var message = new TestEvent("test");
    ScopeContextAccessor.CurrentContext = null;

    // Act
    await decorator.AppendAsync(streamId, message);

    // Assert
    await Assert.That(capturingStore.CapturedEnvelope).IsNotNull();
    var hop = capturingStore.CapturedEnvelope!.Hops[0];
    await Assert.That(hop.Scope).IsNull();
  }

  [Test]
  public async Task AppendAsync_WithMessage_WithNonPropagatingContext_DoesNotPropagateAsync() {
    // Arrange
    var capturingStore = new CapturingEventStore();
    var decorator = new SecurityContextEventStoreDecorator(capturingStore);
    var streamId = Guid.NewGuid();
    var message = new TestEvent("test");

    var scope = new PerspectiveScope { UserId = "user-123", TenantId = "tenant-456" };
    var extraction = new SecurityExtraction {
      Scope = scope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    // shouldPropagate = false
    var immutableContext = new ImmutableScopeContext(extraction, shouldPropagate: false);
    ScopeContextAccessor.CurrentContext = immutableContext;

    try {
      // Act
      await decorator.AppendAsync(streamId, message);

      // Assert
      await Assert.That(capturingStore.CapturedEnvelope).IsNotNull();
      var hop = capturingStore.CapturedEnvelope!.Hops[0];
      await Assert.That(hop.Scope).IsNull();
    } finally {
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  [Test]
  public async Task AppendAsync_WithEnvelope_DelegatesToInnerUnmodifiedAsync() {
    // Arrange
    var capturingStore = new CapturingEventStore();
    var decorator = new SecurityContextEventStoreDecorator(capturingStore);
    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<TestEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent("test"),
      Hops = [
        new MessageHop {
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext { UserId = "original-user" })
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act
    await decorator.AppendAsync(streamId, envelope);

    // Assert
    await Assert.That(capturingStore.CapturedEnvelope).IsSameReferenceAs(envelope);
  }

  [Test]
  public async Task ReadAsync_DelegatesToInnerAsync() {
    // Arrange
    var inner = new FakeEventStore();
    var decorator = new SecurityContextEventStoreDecorator(inner);
    var streamId = Guid.NewGuid();

    // Act - iterate to trigger the async enumerable
    await foreach (var _ in decorator.ReadAsync<TestEvent>(streamId, 0)) {
      // Intentionally empty - just iterating
    }

    // Assert
    await Assert.That(inner.ReadCalled).IsTrue();
  }

  [Test]
  public async Task GetLastSequenceAsync_DelegatesToInnerAsync() {
    // Arrange
    var inner = new FakeEventStore { LastSequenceToReturn = 42 };
    var decorator = new SecurityContextEventStoreDecorator(inner);
    var streamId = Guid.NewGuid();

    // Act
    var result = await decorator.GetLastSequenceAsync(streamId);

    // Assert
    await Assert.That(result).IsEqualTo(42);
    await Assert.That(inner.GetLastSequenceCalled).IsTrue();
  }

  /// <summary>
  /// Test helper that captures the envelope passed to AppendAsync.
  /// </summary>
  private sealed class CapturingEventStore : IEventStore {
#pragma warning disable CA1859 // Using IMessageEnvelope for test flexibility
    public IMessageEnvelope? CapturedEnvelope { get; private set; }
#pragma warning restore CA1859

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken ct = default) {
      CapturedEnvelope = envelope;
      return Task.CompletedTask;
    }

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken ct = default) where TMessage : notnull {
      throw new NotImplementedException("Should not be called - decorator creates envelope");
    }

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken ct = default) => throw new NotImplementedException();
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken ct = default) => Task.FromResult(-1L);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }

  /// <summary>
  /// Test helper for verifying delegation calls.
  /// </summary>
  private sealed class FakeEventStore : IEventStore {
    public bool ReadCalled { get; private set; }
    public bool GetLastSequenceCalled { get; private set; }
    public long LastSequenceToReturn { get; set; } = -1;

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken ct = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken ct = default) where TMessage : notnull => Task.CompletedTask;

    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
      ReadCalled = true;
      await Task.CompletedTask;
      yield break;
    }

    public async IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
      ReadCalled = true;
      await Task.CompletedTask;
      yield break;
    }

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
      ReadCalled = true;
      await Task.CompletedTask;
      yield break;
    }

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken ct = default) {
      ReadCalled = true;
      return Task.FromResult(new List<MessageEnvelope<TMessage>>());
    }

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken ct = default) {
      ReadCalled = true;
      return Task.FromResult(new List<MessageEnvelope<IEvent>>());
    }

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken ct = default) {
      GetLastSequenceCalled = true;
      return Task.FromResult(LastSequenceToReturn);
    }

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }
}
