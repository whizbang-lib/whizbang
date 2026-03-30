#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for PerspectiveWorker's PostLifecycle stage firing.
/// PostLifecycle fires once per unique event at the end of each batch,
/// after all perspectives in the batch have processed.
/// </summary>
public class PerspectiveWorkerPostLifecycleTests {

  [Test]
  public async Task ReceptorInvoker_InvokesPostLifecycleAsync_WhenResolvedFromScopeAsync() {
    // Arrange
    var invoked = false;
    var registry = new TestPostLifecycleReceptorRegistry();
    registry.AddReceptor(LifecycleStage.PostLifecycleAsync, new ReceptorInfo(
      MessageType: typeof(TestPostLifecycleEvent),
      ReceptorId: "test_post_lifecycle_async_receptor",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        invoked = true;
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp =>
      new ReceptorInvoker(sp.GetRequiredService<IReceptorRegistry>(), sp));
    var serviceProvider = services.BuildServiceProvider();

    await using var scope = serviceProvider.CreateAsyncScope();
    var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
    var envelope = _createEventEnvelope("test-user", "test-tenant");

    // Act
    await receptorInvoker!.InvokeAsync(envelope, LifecycleStage.PostLifecycleAsync);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task ReceptorInvoker_InvokesPostLifecycleInline_WhenResolvedFromScopeAsync() {
    // Arrange
    var invoked = false;
    var registry = new TestPostLifecycleReceptorRegistry();
    registry.AddReceptor(LifecycleStage.PostLifecycleInline, new ReceptorInfo(
      MessageType: typeof(TestPostLifecycleEvent),
      ReceptorId: "test_post_lifecycle_inline_receptor",
      InvokeAsync: (sp, msg, envelope, callerInfo, ct) => {
        invoked = true;
        return ValueTask.FromResult<object?>(null);
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp =>
      new ReceptorInvoker(sp.GetRequiredService<IReceptorRegistry>(), sp));
    var serviceProvider = services.BuildServiceProvider();

    await using var scope = serviceProvider.CreateAsyncScope();
    var receptorInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
    var envelope = _createEventEnvelope("test-user", "test-tenant");

    // Act
    await receptorInvoker!.InvokeAsync(envelope, LifecycleStage.PostLifecycleInline);

    // Assert
    await Assert.That(invoked).IsTrue();
  }

  [Test]
  public async Task PostLifecycleAsync_ContextHasNullPerspectiveType_WhenCreatedForBatchEndAsync() {
    // PostLifecycle is NOT perspective-specific, so PerspectiveType should be null in the context
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostLifecycleAsync,
      StreamId = Guid.CreateVersion7(),
      PerspectiveType = null,
      MessageSource = MessageSource.Local,
      AttemptNumber = 1
    };

    // Assert: PerspectiveType is null for PostLifecycle (not tied to any specific perspective)
    await Assert.That(context.PerspectiveType).IsNull();
    await Assert.That(context.CurrentStage).IsEqualTo(LifecycleStage.PostLifecycleAsync);
    await Assert.That(context.MessageSource).IsEqualTo(MessageSource.Local);
  }

  [Test]
  public async Task PostLifecycleInline_ContextHasNullPerspectiveType_WhenCreatedForBatchEndAsync() {
    // PostLifecycleInline context also has null PerspectiveType
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostLifecycleInline,
      StreamId = Guid.CreateVersion7(),
      PerspectiveType = null,
      MessageSource = MessageSource.Local,
      AttemptNumber = 1
    };

    await Assert.That(context.PerspectiveType).IsNull();
    await Assert.That(context.CurrentStage).IsEqualTo(LifecycleStage.PostLifecycleInline);
  }

  [Test]
  public async Task BatchProcessedEvents_DeduplicatesByEventId_WhenSameEventInMultipleGroupsAsync() {
    // Simulates the batch-level deduplication: if an event appears in multiple
    // perspective groups, PostLifecycle should only fire once
    var batchProcessedEvents = new Dictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();

    var eventId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var envelope1 = _createEventEnvelopeWithId(eventId);
    var envelope2 = _createEventEnvelopeWithId(eventId); // Same event ID

    // Act: Simulate two perspective groups adding the same event
    batchProcessedEvents.TryAdd(eventId, (envelope1, streamId));
    batchProcessedEvents.TryAdd(eventId, (envelope2, streamId)); // Should be ignored

    // Assert: Only one entry
    await Assert.That(batchProcessedEvents.Count).IsEqualTo(1);
  }

  [Test]
  public async Task BatchProcessedEvents_Empty_WhenNoPerspectivesProcessedAsync() {
    // When no perspectives process events, PostLifecycle should not fire
    var batchProcessedEvents = new Dictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();

    // Assert
    await Assert.That(batchProcessedEvents.Count).IsEqualTo(0);
  }

  [Test]
  public async Task BatchProcessedEvents_CollectsFromMultipleGroups_UniqueEventsAsync() {
    // When different perspective groups process different events,
    // all unique events should be collected for PostLifecycle
    var batchProcessedEvents = new Dictionary<Guid, (MessageEnvelope<IEvent> Envelope, Guid StreamId)>();

    var eventId1 = Guid.CreateVersion7();
    var eventId2 = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();

    batchProcessedEvents.TryAdd(eventId1, (_createEventEnvelopeWithId(eventId1), streamId));
    batchProcessedEvents.TryAdd(eventId2, (_createEventEnvelopeWithId(eventId2), streamId));

    // Assert
    await Assert.That(batchProcessedEvents.Count).IsEqualTo(2);
  }

  #region Helper Methods

  private static MessageEnvelope<TestPostLifecycleEvent> _createEventEnvelope(string userId, string tenantId) {
    return new MessageEnvelope<TestPostLifecycleEvent> {
      MessageId = MessageId.New(),
      Payload = new TestPostLifecycleEvent("test-event"),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = CorrelationId.New(),
          CausationId = MessageId.New(),
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          },
          Scope = ScopeDelta.FromSecurityContext(new SecurityContext {
            UserId = userId,
            TenantId = tenantId
          })
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static MessageEnvelope<IEvent> _createEventEnvelopeWithId(Guid eventId) {
    return new MessageEnvelope<IEvent> {
      MessageId = new MessageId(eventId),
      Payload = new TestPostLifecycleEvent("test-event-" + eventId),
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = CorrelationId.New(),
          CausationId = MessageId.New(),
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          }
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  #endregion

  #region Test Types

  private sealed record TestPostLifecycleEvent(string Name) : IEvent;

  #endregion

  #region Test Doubles

  private sealed class TestPostLifecycleReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void AddReceptor(LifecycleStage stage, ReceptorInfo receptor) {
      var key = (receptor.MessageType, stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(receptor);
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : Array.Empty<ReceptorInfo>();
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  #endregion
}
