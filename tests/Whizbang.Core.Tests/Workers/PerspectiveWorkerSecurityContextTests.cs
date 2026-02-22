using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for PerspectiveWorker security context establishment before lifecycle receptor invocation.
/// Ensures IMessageContext.UserId is available when lifecycle receptors are invoked.
/// </summary>
/// <docs>workers/perspective-worker#security-context</docs>
public class PerspectiveWorkerSecurityContextTests {

  #region Security Context Tests for _establishSecurityContextAsync

  /// <summary>
  /// Verifies that when a security provider is registered and returns a valid context,
  /// the IScopeContextAccessor.Current is set before lifecycle receptors are invoked.
  /// </summary>
  [Test]
  public async Task PrePerspectiveAsync_WithSecurityProvider_EstablishesSecurityContextAsync() {
    // Arrange
    var expectedUserId = "user-123";
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();

    var capturedUserId = (string?)null;
    var securityContextEstablishedBeforeInvoke = false;

    // Create fake event store that returns a test event
    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestEvent(Guid.CreateVersion7(), "test-data"), expectedUserId);

    // Create security provider that establishes context with UserId
    var scopeContextAccessor = new TestScopeContextAccessor();
    var messageContextAccessor = new TestMessageContextAccessor();

    // Create fake lifecycle invoker that captures the IMessageContext state when invoked
    // Capture the accessor directly to avoid static field pollution
    var lifecycleInvoker = new CapturingLifecycleInvoker(
      onInvoke: (envelope, stage, ctx) => {
        if (stage == LifecycleStage.PrePerspectiveAsync) {
          // Capture the IMessageContext.UserId at the moment of invocation
          capturedUserId = messageContextAccessor.Current?.UserId;
          securityContextEstablishedBeforeInvoke = capturedUserId is not null;
        }
      });

    var securityProvider = new TestSecurityContextProvider(
      userId: expectedUserId,
      scopeContextAccessor: scopeContextAccessor);

    // Create event type provider
    var eventTypeProvider = new TestEventTypeProvider([typeof(TestEvent)]);

    // Create services
    var services = new ServiceCollection();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new FakePerspectiveRunnerRegistry();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    // Return perspective work
    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);
    services.AddSingleton<IScopeContextAccessor>(scopeContextAccessor);
    services.AddSingleton<IMessageContextAccessor>(messageContextAccessor);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      new InstantCompletionStrategy(),
      databaseReadiness,
      lifecycleInvoker,
      eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert
    await Assert.That(securityContextEstablishedBeforeInvoke).IsTrue()
      .Because("Security context should be established BEFORE lifecycle invoker is called");
    await Assert.That(capturedUserId).IsEqualTo(expectedUserId)
      .Because("IMessageContext.UserId should be set from the envelope's security metadata");
  }

  /// <summary>
  /// Verifies that when no security provider is registered, lifecycle receptors are still invoked
  /// (graceful no-op for security context establishment).
  /// </summary>
  [Test]
  public async Task PrePerspectiveAsync_WithoutSecurityProvider_StillInvokesLifecycleReceptorsAsync() {
    // Arrange
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var lifecycleInvoked = false;

    var lifecycleInvoker = new CapturingLifecycleInvoker(
      onInvoke: (envelope, stage, ctx) => {
        if (stage == LifecycleStage.PrePerspectiveAsync) {
          lifecycleInvoked = true;
        }
      });

    // Create fake event store that returns a test event
    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestEvent(Guid.CreateVersion7(), "test-data"));

    // Create event type provider
    var eventTypeProvider = new TestEventTypeProvider([typeof(TestEvent)]);

    // Create services - NO security provider registered
    var services = new ServiceCollection();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new FakePerspectiveRunnerRegistry();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    // NO IMessageSecurityContextProvider registered
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      new InstantCompletionStrategy(),
      databaseReadiness,
      lifecycleInvoker,
      eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert
    await Assert.That(lifecycleInvoked).IsTrue()
      .Because("Lifecycle receptors should still be invoked even without security provider");
  }

  /// <summary>
  /// Verifies that when security provider returns null, IScopeContextAccessor is not set
  /// but lifecycle receptors are still invoked.
  /// </summary>
  [Test]
  public async Task PrePerspectiveAsync_SecurityProviderReturnsNull_DoesNotSetAccessorAsync() {
    // Arrange
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var accessorWasSet = false;

    var scopeContextAccessor = new TestScopeContextAccessor(
      onSet: () => { accessorWasSet = true; });

    var securityProvider = new TestSecurityContextProvider(
      returnsNull: true,
      scopeContextAccessor: scopeContextAccessor);

    var lifecycleInvoker = new CapturingLifecycleInvoker();

    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestEvent(Guid.CreateVersion7(), "test-data"));

    var eventTypeProvider = new TestEventTypeProvider([typeof(TestEvent)]);

    var services = new ServiceCollection();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new FakePerspectiveRunnerRegistry();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);
    services.AddSingleton<IScopeContextAccessor>(scopeContextAccessor);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      new InstantCompletionStrategy(),
      databaseReadiness,
      lifecycleInvoker,
      eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert
    await Assert.That(accessorWasSet).IsFalse()
      .Because("IScopeContextAccessor should not be set when security provider returns null");
  }

  /// <summary>
  /// Verifies that PostPerspectiveInline lifecycle receptors also receive security context.
  /// </summary>
  [Test]
  public async Task PostPerspectiveInline_WithSecurityProvider_EstablishesSecurityContextAsync() {
    // Arrange
    var expectedUserId = "user-456";
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();

    var capturedUserId = (string?)null;
    var postPerspectiveInlineInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var messageContextAccessor = new TestMessageContextAccessor();

    var lifecycleInvoker = new CapturingLifecycleInvoker(
      onInvoke: (envelope, stage, ctx) => {
        if (stage == LifecycleStage.PostPerspectiveInline) {
          capturedUserId = messageContextAccessor.Current?.UserId;
          postPerspectiveInlineInvoked.TrySetResult();
        }
      });

    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestEvent(Guid.CreateVersion7(), "test-data"), expectedUserId);

    var scopeContextAccessor = new TestScopeContextAccessor();
    var securityProvider = new TestSecurityContextProvider(
      userId: expectedUserId,
      scopeContextAccessor: scopeContextAccessor);

    var eventTypeProvider = new TestEventTypeProvider([typeof(TestEvent)]);

    var services = new ServiceCollection();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new FakePerspectiveRunnerRegistry();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);
    services.AddSingleton<IScopeContextAccessor>(scopeContextAccessor);
    services.AddSingleton<IMessageContextAccessor>(messageContextAccessor);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      new InstantCompletionStrategy(),
      databaseReadiness,
      lifecycleInvoker,
      eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);

    // Wait for PostPerspectiveInline to be invoked (happens AFTER completion is reported)
    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
    var completedTask = await Task.WhenAny(postPerspectiveInlineInvoked.Task, timeoutTask);
    if (completedTask == timeoutTask) {
      throw new TimeoutException("PostPerspectiveInline was not invoked within timeout");
    }

    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert
    await Assert.That(capturedUserId).IsEqualTo(expectedUserId)
      .Because("PostPerspectiveInline should have security context established with UserId");
  }

  /// <summary>
  /// Verifies that security context is established for each envelope in a batch,
  /// not just the first one.
  /// </summary>
  [Test]
  public async Task MultipleEnvelopes_EstablishesContextForEachEnvelopeAsync() {
    // Arrange
    var streamId = Guid.CreateVersion7();
    var eventId1 = Guid.CreateVersion7();
    var eventId2 = Guid.CreateVersion7();
    var userId1 = "user-1";
    var userId2 = "user-2";

    var capturedUserIds = new List<string?>();

    var messageContextAccessor = new TestMessageContextAccessor();

    var lifecycleInvoker = new CapturingLifecycleInvoker(
      onInvoke: (envelope, stage, ctx) => {
        if (stage == LifecycleStage.PrePerspectiveAsync) {
          capturedUserIds.Add(messageContextAccessor.Current?.UserId);
        }
      });

    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId1, new TestEvent(Guid.CreateVersion7(), "data-1"), userId1);
    eventStore.AddEvent(streamId, eventId2, new TestEvent(Guid.CreateVersion7(), "data-2"), userId2);

    var scopeContextAccessor = new TestScopeContextAccessor();
    // Security provider returns different UserId based on envelope
    var securityProvider = new EnvelopeAwareSecurityContextProvider(scopeContextAccessor);

    var eventTypeProvider = new TestEventTypeProvider([typeof(TestEvent)]);

    var services = new ServiceCollection();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new FakePerspectiveRunnerRegistry();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);
    services.AddSingleton<IScopeContextAccessor>(scopeContextAccessor);
    services.AddSingleton<IMessageContextAccessor>(messageContextAccessor);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      new InstantCompletionStrategy(),
      databaseReadiness,
      lifecycleInvoker,
      eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert - should have captured two different user IDs
    await Assert.That(capturedUserIds.Count).IsEqualTo(2)
      .Because("Security context should be established for each envelope");
    await Assert.That(capturedUserIds[0]).IsEqualTo(userId1);
    await Assert.That(capturedUserIds[1]).IsEqualTo(userId2);
  }

  /// <summary>
  /// Verifies that when no IMessageContextAccessor is registered, lifecycle still works
  /// (graceful no-op).
  /// </summary>
  [Test]
  public async Task PrePerspectiveAsync_WithoutMessageContextAccessor_StillInvokesLifecycleReceptorsAsync() {
    // Arrange
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var lifecycleInvoked = false;

    var lifecycleInvoker = new CapturingLifecycleInvoker(
      onInvoke: (envelope, stage, ctx) => {
        if (stage == LifecycleStage.PrePerspectiveAsync) {
          lifecycleInvoked = true;
        }
      });

    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestEvent(Guid.CreateVersion7(), "test-data"));

    var scopeContextAccessor = new TestScopeContextAccessor();
    var securityProvider = new TestSecurityContextProvider(
      userId: "test-user",
      scopeContextAccessor: scopeContextAccessor);

    var eventTypeProvider = new TestEventTypeProvider([typeof(TestEvent)]);

    var services = new ServiceCollection();
    var coordinator = new FakeWorkCoordinator();
    var instanceProvider = new FakeServiceInstanceProvider();
    var registry = new FakePerspectiveRunnerRegistry();
    var databaseReadiness = new FakeDatabaseReadinessCheck { IsReady = true };

    coordinator.PerspectiveWorkToReturn = [
      new PerspectiveWork {
        StreamId = streamId,
        PerspectiveName = "Test.FakePerspective",
        LastProcessedEventId = null,
        PartitionNumber = 1
      }
    ];

    services.AddSingleton<IWorkCoordinator>(coordinator);
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IServiceInstanceProvider>(instanceProvider);
    services.AddSingleton<IEventStore>(eventStore);
    services.AddSingleton<IMessageSecurityContextProvider>(securityProvider);
    services.AddSingleton<IScopeContextAccessor>(scopeContextAccessor);
    // NO IMessageContextAccessor registered
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      new InstantCompletionStrategy(),
      databaseReadiness,
      lifecycleInvoker,
      eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert
    await Assert.That(lifecycleInvoked).IsTrue()
      .Because("Lifecycle receptors should still be invoked without IMessageContextAccessor");
  }

  #endregion

  #region Test Fakes

  private sealed record TestEvent(Guid Id, string Data) : IEvent;

  private sealed class CapturingLifecycleInvoker : ILifecycleInvoker {
    private readonly Action<IMessageEnvelope, LifecycleStage, ILifecycleContext?>? _onInvoke;

    public CapturingLifecycleInvoker(
        Action<IMessageEnvelope, LifecycleStage, ILifecycleContext?>? onInvoke = null) {
      _onInvoke = onInvoke;
    }

    public ValueTask InvokeAsync(
        IMessageEnvelope envelope,
        LifecycleStage stage,
        ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      _onInvoke?.Invoke(envelope, stage, context);
      return ValueTask.CompletedTask;
    }
  }

  private sealed class FakeEventStore : IEventStore {
    private readonly List<(Guid StreamId, Guid EventId, IEvent Event, string? UserId)> _events = [];

    public void AddEvent(Guid streamId, Guid eventId, IEvent @event, string? userId = null) {
      _events.Add((streamId, eventId, @event, userId));
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull {
      return Task.CompletedTask;
    }

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) {
      return AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    }

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) {
      return AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    }

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      return AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();
    }

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) {
      return Task.FromResult(new List<MessageEnvelope<TMessage>>());
    }

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      var result = new List<MessageEnvelope<IEvent>>();

      foreach (var (sid, eid, evt, userId) in _events) {
        if (sid == streamId) {
          // Create hop with security context containing UserId
          var securityContext = userId is not null
            ? new Whizbang.Core.Observability.SecurityContext { UserId = userId }
            : null;

          var envelope = new MessageEnvelope<IEvent> {
            MessageId = MessageId.From(eid),
            Payload = evt,
            Hops = [new MessageHop {
              Type = HopType.Current,
              ServiceInstance = ServiceInstanceInfo.Unknown,
              SecurityContext = securityContext
            }]
          };

          result.Add(envelope);
        }
      }

      return Task.FromResult(result);
    }

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) {
      return Task.FromResult(-1L);
    }
  }

  private sealed class TestScopeContext : IScopeContext {
    public required string UserId { get; init; }
    public PerspectiveScope Scope => new();
    public IReadOnlySet<string> Roles => new HashSet<string>();
    public IReadOnlySet<Permission> Permissions => new HashSet<Permission>();
    public IReadOnlySet<SecurityPrincipalId> SecurityPrincipals => new HashSet<SecurityPrincipalId>();
    public IReadOnlyDictionary<string, string> Claims => new Dictionary<string, string>();
    public string? ActualPrincipal => UserId;
    public string? EffectivePrincipal => UserId;
    public SecurityContextType ContextType => SecurityContextType.User;

    public bool HasPermission(Permission permission) => false;
    public bool HasAnyPermission(params Permission[] permissions) => false;
    public bool HasAllPermissions(params Permission[] permissions) => false;
    public bool HasRole(string roleName) => false;
    public bool HasAnyRole(params string[] roleNames) => false;
    public bool IsMemberOfAny(params SecurityPrincipalId[] principals) => false;
    public bool IsMemberOfAll(params SecurityPrincipalId[] principals) => false;
  }

  private sealed class TestSecurityContextProvider : IMessageSecurityContextProvider {
    private readonly string? _userId;
    private readonly bool _returnsNull;
    private readonly IScopeContextAccessor? _scopeContextAccessor;

    public TestSecurityContextProvider(
        string? userId = null,
        bool returnsNull = false,
        IScopeContextAccessor? scopeContextAccessor = null) {
      _userId = userId;
      _returnsNull = returnsNull;
      _scopeContextAccessor = scopeContextAccessor;
    }

    public ValueTask<IScopeContext?> EstablishContextAsync(
        IMessageEnvelope envelope,
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken = default) {
      if (_returnsNull) {
        return ValueTask.FromResult<IScopeContext?>(null);
      }

      var context = new TestScopeContext { UserId = _userId ?? "default-user" };

      // Set the accessor if provided (simulates what happens in real implementation)
      if (_scopeContextAccessor is not null) {
        _scopeContextAccessor.Current = context;
      }

      return ValueTask.FromResult<IScopeContext?>(context);
    }
  }

  private sealed class EnvelopeAwareSecurityContextProvider : IMessageSecurityContextProvider {
    private readonly IScopeContextAccessor _scopeContextAccessor;

    public EnvelopeAwareSecurityContextProvider(IScopeContextAccessor scopeContextAccessor) {
      _scopeContextAccessor = scopeContextAccessor;
    }

    public ValueTask<IScopeContext?> EstablishContextAsync(
        IMessageEnvelope envelope,
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken = default) {
      // Extract UserId from envelope's security context metadata
      var existingContext = envelope.GetCurrentSecurityContext();
      var userId = existingContext?.UserId ?? "unknown";

      var context = new TestScopeContext { UserId = userId };
      _scopeContextAccessor.Current = context;

      return ValueTask.FromResult<IScopeContext?>(context);
    }
  }

  private sealed class TestScopeContextAccessor : IScopeContextAccessor {
    private readonly Action? _onSet;
    private IScopeContext? _current;

    public TestScopeContextAccessor(Action? onSet = null) {
      _onSet = onSet;
    }

    public IScopeContext? Current {
      get => _current;
      set {
        _onSet?.Invoke();
        _current = value;
      }
    }
  }

  private sealed class TestMessageContextAccessor : IMessageContextAccessor {
    public IMessageContext? Current { get; set; }
  }

  private sealed class TestEventTypeProvider : IEventTypeProvider {
    private readonly IReadOnlyList<Type> _eventTypes;

    public TestEventTypeProvider(IReadOnlyList<Type> eventTypes) {
      _eventTypes = eventTypes;
    }

    public IReadOnlyList<Type> GetEventTypes() => _eventTypes;
  }

  private sealed class FakeWorkCoordinator : IWorkCoordinator {
    private readonly TaskCompletionSource _completionReported = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<PerspectiveWork> PerspectiveWorkToReturn { get; set; } = [];

    public async Task WaitForCompletionReportedAsync(TimeSpan timeout) {
      using var cts = new CancellationTokenSource(timeout);
      try {
        await _completionReported.Task.WaitAsync(cts.Token);
      } catch (OperationCanceledException) {
        throw new TimeoutException($"Completion was not reported within {timeout}");
      }
    }

    public Task<WorkBatch> ProcessWorkBatchAsync(
        ProcessWorkBatchRequest request,
        CancellationToken cancellationToken = default) {
      var work = new List<PerspectiveWork>(PerspectiveWorkToReturn);
      PerspectiveWorkToReturn.Clear();

      return Task.FromResult(new WorkBatch {
        OutboxWork = [],
        InboxWork = [],
        PerspectiveWork = work
      });
    }

    public Task ReportPerspectiveCompletionAsync(
        PerspectiveCheckpointCompletion completion,
        CancellationToken cancellationToken = default) {
      _completionReported.TrySetResult();
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
        PerspectiveCheckpointFailure failure,
        CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCheckpointInfo?> GetPerspectiveCheckpointAsync(
        Guid streamId,
        string perspectiveName,
        CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCheckpointInfo?>(null);
    }
  }

  private sealed class FakeServiceInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName { get; } = "TestService";
    public string HostName { get; } = "test-host";
    public int ProcessId { get; } = 12345;

    public ServiceInstanceInfo ToInfo() {
      return new ServiceInstanceInfo {
        ServiceName = ServiceName,
        InstanceId = InstanceId,
        HostName = HostName,
        ProcessId = ProcessId
      };
    }
  }

  private sealed class FakeDatabaseReadinessCheck : IDatabaseReadinessCheck {
    public bool IsReady { get; set; } = true;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) {
      return Task.FromResult(IsReady);
    }
  }

  private sealed class FakePerspectiveRunnerRegistry : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) {
      return new FakePerspectiveRunner();
    }

    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() {
      return [new PerspectiveRegistrationInfo("Test.FakePerspective", "global::Test.FakePerspective", "global::Test.FakeModel", ["global::Test.FakeEvent"])];
    }

    public IReadOnlyList<Type> GetEventTypes() => [typeof(TestEvent)];
  }

  private sealed class FakePerspectiveRunner : IPerspectiveRunner {
    public Task<PerspectiveCheckpointCompletion> RunAsync(
        Guid streamId,
        string perspectiveName,
        Guid? lastProcessedEventId,
        CancellationToken cancellationToken) {
      return Task.FromResult(new PerspectiveCheckpointCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.CreateVersion7(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }
  }

  #endregion
}
