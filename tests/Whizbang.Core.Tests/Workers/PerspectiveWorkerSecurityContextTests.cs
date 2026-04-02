using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
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
  public async Task PrePerspectiveDetached_WithSecurityProvider_EstablishesSecurityContextAsync() {
    // Arrange
    const string expectedUserId = "user-123";
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
        if (stage == LifecycleStage.PrePerspectiveDetached) {
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
    services.AddScoped<IReceptorInvoker>(_ => lifecycleInvoker);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    await worker.DrainDetachedAsync();
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
  public async Task PrePerspectiveDetached_WithoutSecurityProvider_StillInvokesLifecycleReceptorsAsync() {
    // Arrange
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var lifecycleInvoked = false;

    var lifecycleInvoker = new CapturingLifecycleInvoker(
      onInvoke: (envelope, stage, ctx) => {
        if (stage == LifecycleStage.PrePerspectiveDetached) {
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
    services.AddScoped<IReceptorInvoker>(_ => lifecycleInvoker);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    await worker.DrainDetachedAsync();
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
  public async Task PrePerspectiveDetached_SecurityProviderReturnsNull_DoesNotSetAccessorAsync() {
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
    services.AddScoped<IReceptorInvoker>(_ => lifecycleInvoker);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
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
    const string expectedUserId = "user-456";
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
    services.AddScoped<IReceptorInvoker>(_ => lifecycleInvoker);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
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
    const string userId1 = "user-1";
    const string userId2 = "user-2";

    var capturedUserIds = new List<string?>();

    var messageContextAccessor = new TestMessageContextAccessor();

    var lifecycleInvoker = new CapturingLifecycleInvoker(
      onInvoke: (envelope, stage, ctx) => {
        if (stage == LifecycleStage.PrePerspectiveDetached) {
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
    services.AddScoped<IReceptorInvoker>(_ => lifecycleInvoker);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    await worker.DrainDetachedAsync();
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
  public async Task PrePerspectiveDetached_WithoutMessageContextAccessor_StillInvokesLifecycleReceptorsAsync() {
    // Arrange
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();
    var lifecycleInvoked = false;

    var lifecycleInvoker = new CapturingLifecycleInvoker(
      onInvoke: (envelope, stage, ctx) => {
        if (stage == LifecycleStage.PrePerspectiveDetached) {
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
    services.AddScoped<IReceptorInvoker>(_ => lifecycleInvoker);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    await worker.DrainDetachedAsync();
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

  #region TenantContext Propagation Tests (Priority Fix)

  /// <summary>
  /// CRITICAL BUG FIX TEST: Verifies that when the security provider establishes a context
  /// with TenantId, but the envelope's hops have NO scope (GetCurrentScope returns null),
  /// the MessageContext should use the extractor's result, NOT the envelope's null scope.
  /// </summary>
  /// <remarks>
  /// This is the root cause of TenantContext being null in PostPerspectiveDetached handlers.
  /// The PerspectiveWorker._establishSecurityContextAsync was reading from envelope.GetCurrentScope()
  /// instead of using the securityContext returned by EstablishContextAsync.
  /// </remarks>
  [Test]
  public async Task EstablishSecurityContext_WhenExtractorSucceeds_ButEnvelopeHasNoScope_UsesExtractorResultForMessageContextAsync() {
    // Arrange: Event envelope with NO scope in hops (GetCurrentScope() returns null)
    const string expectedTenantId = "tenant-123";
    const string expectedUserId = "user-456";
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();

    var capturedTenantId = (string?)null;
    var capturedUserId = (string?)null;
    IScopeContext? capturedScopeContext = null;

    var messageContextAccessor = new TestMessageContextAccessor();
    var scopeContextAccessor = new TestScopeContextAccessor();

    // Create lifecycle invoker that captures the IMessageContext state
    var lifecycleInvoker = new CapturingLifecycleInvoker(
      onInvoke: (envelope, stage, ctx) => {
        if (stage == LifecycleStage.PrePerspectiveDetached) {
          capturedTenantId = messageContextAccessor.Current?.TenantId;
          capturedUserId = messageContextAccessor.Current?.UserId;
          capturedScopeContext = messageContextAccessor.Current?.ScopeContext;
        }
      });

    // Event store returns events WITHOUT scope in hops
    var eventStore = new FakeEventStoreNoScope();
    eventStore.AddEvent(streamId, eventId, new TestEvent(Guid.CreateVersion7(), "test-data"));

    // Security provider returns full context WITH TenantId (simulating MessageHopSecurityExtractor success)
    var securityProvider = new TestSecurityContextProviderWithTenant(
      tenantId: expectedTenantId,
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
    services.AddScoped<IReceptorInvoker>(_ => lifecycleInvoker);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
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

    // Assert: MessageContext should use extractor result, NOT envelope.GetCurrentScope() (which is null)
    await Assert.That(capturedTenantId).IsEqualTo(expectedTenantId)
      .Because("MessageContext.TenantId should come from security extractor, not envelope.GetCurrentScope()");
    await Assert.That(capturedUserId).IsEqualTo(expectedUserId)
      .Because("MessageContext.UserId should come from security extractor, not envelope.GetCurrentScope()");
    await Assert.That(capturedScopeContext).IsNotNull()
      .Because("MessageContext.ScopeContext should be set from security extractor result");
    await Assert.That(capturedScopeContext?.Scope?.TenantId).IsEqualTo(expectedTenantId)
      .Because("ScopeContext.Scope.TenantId should match the extractor result");
  }

  /// <summary>
  /// Verifies that when the security provider returns null (no extraction),
  /// the MessageContext falls back to envelope.GetCurrentScope() for scope values.
  /// </summary>
  [Test]
  public async Task EstablishSecurityContext_WhenExtractorFails_FallsBackToEnvelopeGetCurrentScopeAsync() {
    // Arrange: Envelope WITH scope in hops (so GetCurrentScope returns valid data)
    const string expectedTenantId = "hop-tenant";
    const string expectedUserId = "hop-user";
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();

    var capturedTenantId = (string?)null;
    var capturedUserId = (string?)null;

    var messageContextAccessor = new TestMessageContextAccessor();
    var scopeContextAccessor = new TestScopeContextAccessor();

    var lifecycleInvoker = new CapturingLifecycleInvoker(
      onInvoke: (envelope, stage, ctx) => {
        if (stage == LifecycleStage.PrePerspectiveDetached) {
          capturedTenantId = messageContextAccessor.Current?.TenantId;
          capturedUserId = messageContextAccessor.Current?.UserId;
        }
      });

    // Event store returns events WITH scope in hops
    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestEvent(Guid.CreateVersion7(), "test-data"), expectedUserId, expectedTenantId);

    // Security provider returns NULL (simulating extraction failure)
    var securityProvider = new TestSecurityContextProvider(
      returnsNull: true,
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
    services.AddScoped<IReceptorInvoker>(_ => lifecycleInvoker);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
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

    // Assert: Should fall back to envelope.GetCurrentScope()
    await Assert.That(capturedTenantId).IsEqualTo(expectedTenantId)
      .Because("When extractor fails, MessageContext.TenantId should fall back to envelope.GetCurrentScope()");
    await Assert.That(capturedUserId).IsEqualTo(expectedUserId)
      .Because("When extractor fails, MessageContext.UserId should fall back to envelope.GetCurrentScope()");
  }

  /// <summary>
  /// CRITICAL FIX TEST: Verifies that InitiatingContext is set on IScopeContextAccessor.
  /// This is required for CascadeContext.GetSecurityFromAmbient() to work correctly when
  /// lifecycle handlers append events via SecurityContextEventStoreDecorator.
  /// </summary>
  [Test]
  public async Task EstablishSecurityContext_SetsInitiatingContextOnScopeContextAccessorAsync() {
    // Arrange
    const string expectedTenantId = "tenant-from-initiating";
    const string expectedUserId = "user-from-initiating";
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();

    IMessageContext? capturedInitiatingContext = null;
    IScopeContext? capturedScopeContextFromAccessor = null;

    var scopeContextAccessor = new TestScopeContextAccessor();
    var messageContextAccessor = new TestMessageContextAccessor();

    // Lifecycle invoker captures the InitiatingContext state during invocation
    var lifecycleInvoker = new CapturingLifecycleInvoker(
      onInvoke: (envelope, stage, ctx) => {
        if (stage == LifecycleStage.PrePerspectiveDetached) {
          capturedInitiatingContext = scopeContextAccessor.InitiatingContext;
          capturedScopeContextFromAccessor = scopeContextAccessor.Current;
        }
      });

    // Event store returns events WITHOUT scope in hops
    var eventStore = new FakeEventStoreNoScope();
    eventStore.AddEvent(streamId, eventId, new TestEvent(Guid.CreateVersion7(), "test-data"));

    // Security provider returns full context WITH TenantId
    var securityProvider = new TestSecurityContextProviderWithTenant(
      tenantId: expectedTenantId,
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
    services.AddScoped<IReceptorInvoker>(_ => lifecycleInvoker);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
      eventTypeProvider
    );

    // Act
    using var cts = new CancellationTokenSource();
    var workerTask = worker.StartAsync(cts.Token);
    await coordinator.WaitForCompletionReportedAsync(timeout: TimeSpan.FromSeconds(5));
    await worker.DrainDetachedAsync();
    cts.Cancel();

    try {
      await workerTask;
    } catch (OperationCanceledException) {
      // Expected during shutdown
    }

    // Assert: InitiatingContext should be set with proper TenantId and UserId
    await Assert.That(capturedInitiatingContext).IsNotNull()
      .Because("InitiatingContext should be set on IScopeContextAccessor for CascadeContext.GetSecurityFromAmbient()");
    await Assert.That(capturedInitiatingContext?.TenantId).IsEqualTo(expectedTenantId)
      .Because("InitiatingContext.TenantId should come from the established security context");
    await Assert.That(capturedInitiatingContext?.UserId).IsEqualTo(expectedUserId)
      .Because("InitiatingContext.UserId should come from the established security context");
    await Assert.That(capturedInitiatingContext?.ScopeContext).IsNotNull()
      .Because("InitiatingContext.ScopeContext should be set for cascade propagation");

    // Also verify that IScopeContextAccessor.Current returns the scope
    await Assert.That(capturedScopeContextFromAccessor).IsNotNull()
      .Because("IScopeContextAccessor.Current should return the established scope");
  }

  /// <summary>
  /// CRITICAL BUG FIX TEST: Verifies that ISecurityContextCallbacks are invoked when
  /// the security provider returns null (extraction fails) BUT the envelope has scope in hops.
  /// This is required for UserContextManagerCallback to set TenantContext in PostPerspectiveDetached handlers.
  /// </summary>
  /// <remarks>
  /// Root cause: DefaultMessageSecurityContextProvider only invokes callbacks when extraction succeeds.
  /// When extraction fails, callbacks are NOT invoked, so UserContextManagerCallback doesn't run,
  /// and _userContextManager.TenantContext remains null.
  ///
  /// Fix: PerspectiveWorker should manually invoke callbacks with the envelope's scope when
  /// securityProvider returns null but envelope.GetCurrentScope() has data.
  /// </remarks>
  [Test]
  public async Task EstablishSecurityContext_WhenExtractorFailsButEnvelopeHasScope_InvokesCallbacksWithEnvelopeScopeAsync() {
    // Arrange: Envelope WITH scope in hops (GetCurrentScope returns valid data)
    const string expectedTenantId = "hop-tenant-callback";
    const string expectedUserId = "hop-user-callback";
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();

    // Track whether the callback was invoked and with what scope
    var callbackInvoked = false;
    string? callbackTenantId = null;
    string? callbackUserId = null;

    var testCallback = new TestSecurityContextCallback(
      onContextEstablished: (context, _, _, _) => {
        callbackInvoked = true;
        callbackTenantId = context.Scope.TenantId;
        callbackUserId = context.Scope.UserId;
        return ValueTask.CompletedTask;
      });

    var messageContextAccessor = new TestMessageContextAccessor();
    var scopeContextAccessor = new TestScopeContextAccessor();

    var lifecycleInvoker = new CapturingLifecycleInvoker();

    // Event store returns events WITH scope in hops
    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestEvent(Guid.CreateVersion7(), "test-data"), expectedUserId, expectedTenantId);

    // Security provider returns NULL (simulating extraction failure)
    var securityProvider = new TestSecurityContextProvider(
      returnsNull: true,
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
    // Register callback - this is the key: callbacks should be invoked even when extraction fails
    services.AddSingleton<ISecurityContextCallback>(testCallback);
    services.AddScoped<IReceptorInvoker>(_ => lifecycleInvoker);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
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

    // Assert: Callback should have been invoked with envelope's scope
    await Assert.That(callbackInvoked).IsTrue()
      .Because("ISecurityContextCallback should be invoked even when extraction fails but envelope has scope");
    await Assert.That(callbackTenantId).IsEqualTo(expectedTenantId)
      .Because("Callback should receive TenantId from envelope.GetCurrentScope()");
    await Assert.That(callbackUserId).IsEqualTo(expectedUserId)
      .Because("Callback should receive UserId from envelope.GetCurrentScope()");
  }

  /// <summary>
  /// Unit test: Verifies ScopeContext is correctly wrapped in ImmutableScopeContext.
  /// This is a direct test of the ImmutableScopeContext creation logic.
  /// </summary>
  [Test]
  public async Task ImmutableScopeContext_FromScopeContext_HasPropagationEnabledAsync() {
    // Arrange: Create a ScopeContext (like what envelope.GetCurrentScope() returns)
    var originalScope = new ScopeContext {
      Scope = new Whizbang.Core.Lenses.PerspectiveScope { TenantId = "test-tenant", UserId = "test-user" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };

    // Act: Convert to ImmutableScopeContext (like PerspectiveWorker does)
    var extraction = new SecurityExtraction {
      Scope = originalScope.Scope,
      Roles = originalScope.Roles,
      Permissions = originalScope.Permissions,
      SecurityPrincipals = originalScope.SecurityPrincipals,
      Claims = originalScope.Claims,
      ActualPrincipal = originalScope.ActualPrincipal,
      EffectivePrincipal = originalScope.EffectivePrincipal,
      ContextType = originalScope.ContextType,
      Source = "EnvelopeHop"
    };
    var immutableScope = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Set on the static accessor (like PerspectiveWorker does)
    ScopeContextAccessor.CurrentContext = immutableScope;

    // Assert: CascadeContext.GetSecurityFromAmbient() should find it
    var securityFromAmbient = Whizbang.Core.Observability.CascadeContext.GetSecurityFromAmbient();

    await Assert.That(ScopeContextAccessor.CurrentContext).IsNotNull();
    await Assert.That(ScopeContextAccessor.CurrentContext).IsTypeOf<ImmutableScopeContext>();
    await Assert.That(securityFromAmbient).IsNotNull()
      .Because("CascadeContext.GetSecurityFromAmbient() should find ImmutableScopeContext with ShouldPropagate=true");
    await Assert.That(securityFromAmbient!.TenantId).IsEqualTo("test-tenant");
    await Assert.That(securityFromAmbient!.UserId).IsEqualTo("test-user");

    // Cleanup: Reset static accessor
    ScopeContextAccessor.CurrentContext = null;
  }

  /// <summary>
  /// CRITICAL FIX TEST: Verifies envelope.GetCurrentScope() returns proper scope from hops.
  /// Tests the FakeEventStore and ScopeDelta.ApplyTo logic.
  /// </summary>
  [Test]
  public async Task FakeEventStore_GetEventsBetweenPolymorphic_ReturnsEnvelopesWithScopeInHopsAsync() {
    // Arrange
    const string expectedTenantId = "test-tenant-from-hops";
    const string expectedUserId = "test-user-from-hops";
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();

    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestEvent(Guid.CreateVersion7(), "test-data"), expectedUserId, expectedTenantId);

    // Act: Get events from the fake store (this is what PerspectiveWorker does)
    var envelopes = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: null,
      upToEventId: eventId,
      eventTypes: [typeof(TestEvent)]);

    // Assert: Envelope should have scope in hops
    await Assert.That(envelopes.Count).IsEqualTo(1);
    var envelope = envelopes[0];

    // Check hops have scope
    await Assert.That(envelope.Hops.Count).IsEqualTo(1);
    await Assert.That(envelope.Hops[0].Scope).IsNotNull()
      .Because("FakeEventStore should add ScopeDelta to hops");

    // Check GetCurrentScope() returns the scope
    var scopeFromEnvelope = envelope.GetCurrentScope();
    await Assert.That(scopeFromEnvelope).IsNotNull()
      .Because("envelope.GetCurrentScope() should return scope from hops");
    await Assert.That(scopeFromEnvelope!.Scope.TenantId).IsEqualTo(expectedTenantId);
    await Assert.That(scopeFromEnvelope!.Scope.UserId).IsEqualTo(expectedUserId);
  }

  /// <summary>
  /// CRITICAL FIX TEST: Verifies that when extraction fails but envelope has scope,
  /// the scope is wrapped in ImmutableScopeContext with ShouldPropagate=true so that
  /// CascadeContext.GetSecurityFromAmbient() can find it for SecurityContextEventStoreDecorator.
  /// </summary>
  /// <remarks>
  /// This is a simplified test that directly calls the fix logic without PerspectiveWorker.
  /// </remarks>
  [Test]
  public async Task EstablishSecurityContext_WhenEnvelopeHasScope_WrapsInImmutableScopeContextWithPropagationAsync() {
    // Arrange: Create envelope with scope in hops (like real events in database)
    const string expectedTenantId = "propagation-test-tenant";
    const string expectedUserId = "propagation-test-user";

    var scopeDelta = ScopeDelta.FromSecurityContext(new Whizbang.Core.Observability.SecurityContext {
      TenantId = expectedTenantId,
      UserId = expectedUserId
    });

    var envelope = new MessageEnvelope<IEvent> {
      MessageId = MessageId.New(),
      Payload = new TestEvent(Guid.CreateVersion7(), "test-data"),
      Hops = [new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Scope = scopeDelta
      }],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    // Act: Simulate what PerspectiveWorker._establishSecurityContextAsync does when extraction fails
    var scopeFromEnvelope = envelope.GetCurrentScope();

    // This is the fix: wrap in ImmutableScopeContext with propagation enabled
    await Assert.That(scopeFromEnvelope).IsNotNull();

    var extraction = new SecurityExtraction {
      Scope = scopeFromEnvelope!.Scope,
      Roles = scopeFromEnvelope.Roles,
      Permissions = scopeFromEnvelope.Permissions,
      SecurityPrincipals = scopeFromEnvelope.SecurityPrincipals,
      Claims = scopeFromEnvelope.Claims,
      ActualPrincipal = scopeFromEnvelope.ActualPrincipal,
      EffectivePrincipal = scopeFromEnvelope.EffectivePrincipal,
      ContextType = scopeFromEnvelope.ContextType,
      Source = "EnvelopeHop"
    };
    var immutableScope = new ImmutableScopeContext(extraction, shouldPropagate: true);

    // Set on the static accessor (like PerspectiveWorker does)
    ScopeContextAccessor.CurrentContext = immutableScope;

    // Assert: CascadeContext.GetSecurityFromAmbient() should find it
    var securityFromAmbient = Whizbang.Core.Observability.CascadeContext.GetSecurityFromAmbient();

    await Assert.That(securityFromAmbient).IsNotNull()
      .Because("CascadeContext.GetSecurityFromAmbient() should find ImmutableScopeContext with ShouldPropagate=true");
    await Assert.That(securityFromAmbient!.TenantId).IsEqualTo(expectedTenantId);
    await Assert.That(securityFromAmbient!.UserId).IsEqualTo(expectedUserId);

    // Cleanup
    ScopeContextAccessor.CurrentContext = null;
  }

  /// <summary>
  /// Verifies that when both extraction succeeds AND envelope has scope,
  /// callbacks are only invoked once (via the security provider, not again in PerspectiveWorker).
  /// </summary>
  [Test]
  public async Task EstablishSecurityContext_WhenExtractorSucceeds_DoesNotInvokeCallbacksTwiceAsync() {
    // Arrange
    var streamId = Guid.CreateVersion7();
    var eventId = Guid.CreateVersion7();

    var callbackInvocationCount = 0;

    var testCallback = new TestSecurityContextCallback(
      onContextEstablished: (_, _, _, _) => {
        Interlocked.Increment(ref callbackInvocationCount);
        return ValueTask.CompletedTask;
      });

    var messageContextAccessor = new TestMessageContextAccessor();
    var scopeContextAccessor = new TestScopeContextAccessor();

    var lifecycleInvoker = new CapturingLifecycleInvoker();

    // Event store returns events WITH scope in hops
    var eventStore = new FakeEventStore();
    eventStore.AddEvent(streamId, eventId, new TestEvent(Guid.CreateVersion7(), "test-data"), "user-1", "tenant-1");

    // Security provider returns valid context (extraction succeeds)
    var securityProvider = new TestSecurityContextProviderWithTenant(
      tenantId: "tenant-from-extractor",
      userId: "user-from-extractor",
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
    services.AddSingleton<ISecurityContextCallback>(testCallback);
    services.AddScoped<IReceptorInvoker>(_ => lifecycleInvoker);
    services.AddLogging();

    var serviceProvider = services.BuildServiceProvider();

    var worker = new PerspectiveWorker(
      instanceProvider,
      serviceProvider.GetRequiredService<IServiceScopeFactory>(),
      Options.Create(new PerspectiveWorkerOptions { PollingIntervalMilliseconds = 50 }),
      tracingOptions: null,
      new InstantCompletionStrategy(),
      databaseReadiness,
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

    // Assert: Callback should only be invoked once (by the security provider)
    // Note: This may be > 1 if multiple events are processed, but should be exactly 1 per event
    await Assert.That(callbackInvocationCount).IsLessThanOrEqualTo(1)
      .Because("Callbacks should not be invoked twice for the same envelope (once by provider, once by worker)");
  }

  #endregion

  #region Test Fakes

  private sealed record TestEvent(Guid Id, string Data) : IEvent;

  private sealed class CapturingLifecycleInvoker(
      Action<IMessageEnvelope, LifecycleStage, ILifecycleContext?>? onInvoke = null) : IReceptorInvoker {
    private readonly Action<IMessageEnvelope, LifecycleStage, ILifecycleContext?>? _onInvoke = onInvoke;

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
    private readonly List<(Guid StreamId, Guid EventId, IEvent Event, string? UserId, string? TenantId)> _events = [];

    public void AddEvent(Guid streamId, Guid eventId, IEvent @event, string? userId = null, string? tenantId = null) {
      _events.Add((streamId, eventId, @event, userId, tenantId));
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

      foreach (var (sid, eid, evt, userId, tenantId) in _events) {
        if (sid == streamId) {
          // Create hop with security context containing UserId and TenantId
          var scopeDelta = (userId is not null || tenantId is not null)
            ? ScopeDelta.FromSecurityContext(new Whizbang.Core.Observability.SecurityContext { UserId = userId, TenantId = tenantId })
            : null;

          var envelope = new MessageEnvelope<IEvent> {
            MessageId = MessageId.From(eid),
            Payload = evt,
            Hops = [new MessageHop {
              Type = HopType.Current,
              ServiceInstance = ServiceInstanceInfo.Unknown,
              Scope = scopeDelta
            }],
            DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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

  /// <summary>
  /// Event store that returns events WITHOUT scope in hops (GetCurrentScope returns null).
  /// Used to test that security provider result is used instead of envelope scope.
  /// </summary>
  private sealed class FakeEventStoreNoScope : IEventStore {
    private readonly List<(Guid StreamId, Guid EventId, IEvent Event)> _events = [];

    public void AddEvent(Guid streamId, Guid eventId, IEvent @event) {
      _events.Add((streamId, eventId, @event));
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

      foreach (var (sid, eid, evt) in _events) {
        if (sid == streamId) {
          // CRITICAL: Create envelope with NO scope in hops
          // This simulates events that were stored before security scope propagation
          var envelope = new MessageEnvelope<IEvent> {
            MessageId = MessageId.From(eid),
            Payload = evt,
            Hops = [new MessageHop {
              Type = HopType.Current,
              ServiceInstance = ServiceInstanceInfo.Unknown,
              Scope = null  // NO SCOPE - GetCurrentScope() will return null
            }],
            DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
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
    public PerspectiveScope Scope => new() { UserId = UserId };  // Include UserId in Scope
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

  private sealed class TestSecurityContextProvider(
      string? userId = null,
      bool returnsNull = false,
      IScopeContextAccessor? scopeContextAccessor = null) : IMessageSecurityContextProvider {
    private readonly string? _userId = userId;
    private readonly bool _returnsNull = returnsNull;
    private readonly IScopeContextAccessor? _scopeContextAccessor = scopeContextAccessor;

    public ValueTask<IScopeContext?> EstablishContextAsync(
        IMessageEnvelope envelope,
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken = default) {
      if (_returnsNull) {
        return ValueTask.FromResult<IScopeContext?>(null);
      }

      var context = new TestScopeContext { UserId = _userId ?? "default-user" };

      // Set the accessor if provided (simulates what happens in real implementation)
      _scopeContextAccessor?.Current = context;

      return ValueTask.FromResult<IScopeContext?>(context);
    }
  }

  /// <summary>
  /// Security provider that returns scope with BOTH UserId AND TenantId.
  /// Used to test that extractor result is properly used in MessageContext.
  /// </summary>
  private sealed class TestSecurityContextProviderWithTenant(
      string tenantId,
      string userId,
      IScopeContextAccessor? scopeContextAccessor = null) : IMessageSecurityContextProvider {
    private readonly string _tenantId = tenantId;
    private readonly string _userId = userId;
    private readonly IScopeContextAccessor? _scopeContextAccessor = scopeContextAccessor;

    public ValueTask<IScopeContext?> EstablishContextAsync(
        IMessageEnvelope envelope,
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken = default) {
      var context = new TestScopeContextWithTenant {
        UserId = _userId,
        TenantId = _tenantId
      };

      // Set the accessor if provided (simulates what happens in real implementation)
      _scopeContextAccessor?.Current = context;

      return ValueTask.FromResult<IScopeContext?>(context);
    }
  }

  /// <summary>
  /// Scope context with BOTH UserId and TenantId populated.
  /// </summary>
  private sealed class TestScopeContextWithTenant : IScopeContext {
    public required string UserId { get; init; }
    public required string TenantId { get; init; }
    public PerspectiveScope Scope => new() { UserId = UserId, TenantId = TenantId };
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

  /// <summary>
  /// Test callback that captures invocations for assertions.
  /// </summary>
  private sealed class TestSecurityContextCallback(
      Func<IScopeContext, IMessageEnvelope, IServiceProvider, CancellationToken, ValueTask>? onContextEstablished = null) : ISecurityContextCallback {
    private readonly Func<IScopeContext, IMessageEnvelope, IServiceProvider, CancellationToken, ValueTask>? _onContextEstablished = onContextEstablished;

    public ValueTask OnContextEstablishedAsync(
        IScopeContext context,
        IMessageEnvelope envelope,
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken = default) {
      return _onContextEstablished?.Invoke(context, envelope, scopedProvider, cancellationToken) ?? ValueTask.CompletedTask;
    }
  }

  private sealed class EnvelopeAwareSecurityContextProvider(IScopeContextAccessor scopeContextAccessor) : IMessageSecurityContextProvider {
    private readonly IScopeContextAccessor _scopeContextAccessor = scopeContextAccessor;

    public ValueTask<IScopeContext?> EstablishContextAsync(
        IMessageEnvelope envelope,
        IServiceProvider scopedProvider,
        CancellationToken cancellationToken = default) {
      // Extract UserId from envelope's security context metadata
      var existingContext = envelope.GetCurrentScope();
      var userId = existingContext?.Scope?.UserId ?? "unknown";

      var context = new TestScopeContext { UserId = userId };
      _scopeContextAccessor.Current = context;

      return ValueTask.FromResult<IScopeContext?>(context);
    }
  }

  private sealed class TestScopeContextAccessor(Action? onSet = null) : IScopeContextAccessor {
    private readonly Action? _onSet = onSet;
    private IScopeContext? _current;
    private IMessageContext? _initiatingContext;

    public IScopeContext? Current {
      get => _current;
      set {
        _onSet?.Invoke();
        _current = value;
      }
    }

    public IMessageContext? InitiatingContext {
      get => _initiatingContext;
      set => _initiatingContext = value;
    }
  }

  private sealed class TestMessageContextAccessor : IMessageContextAccessor {
    public IMessageContext? Current { get; set; }
  }

  private sealed class TestEventTypeProvider(IReadOnlyList<Type> eventTypes) : IEventTypeProvider {
    private readonly IReadOnlyList<Type> _eventTypes = eventTypes;

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
        PerspectiveCursorCompletion completion,
        CancellationToken cancellationToken = default) {
      _completionReported.TrySetResult();
      return Task.CompletedTask;
    }

    public Task ReportPerspectiveFailureAsync(
        PerspectiveCursorFailure failure,
        CancellationToken cancellationToken = default) {
      return Task.CompletedTask;
    }

    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId,
        string perspectiveName,
        CancellationToken cancellationToken = default) {
      return Task.FromResult<PerspectiveCursorInfo?>(null);
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
    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId,
        string perspectiveName,
        Guid? lastProcessedEventId,
        CancellationToken cancellationToken) {
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.CreateVersion7(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
  }

  #endregion
}
