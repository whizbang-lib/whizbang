using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.Security.Extractors;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for the cascade-to-outbox flow.
/// These tests verify that events returned from receptors are properly
/// cascaded to the outbox table via the EFCore work coordinator.
/// </summary>
/// <remarks>
/// This test suite was created to diagnose a bug where events returned from
/// non-void receptors were not appearing in the outbox table.
/// The JDNext UserService exhibited this behavior with CreateTenantCommandHandler.
/// </remarks>
public class CascadeToOutboxIntegrationTests : EFCoreTestBase {
  #region Test Messages

  /// <summary>
  /// Test command to be handled by a non-void receptor.
  /// The pattern matches JDNext's CreateTenantCommand usage.
  /// </summary>
  public record CascadeTestCommand([property: StreamId] Guid Id);

  /// <summary>
  /// Test event returned by the receptor.
  /// Default routing is Outbox (system default) - should end up in wh_outbox table.
  /// </summary>
  public record CascadeTestEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>
  /// Test event with explicit Local routing for control case.
  /// </summary>
  [DefaultRouting(DispatchModes.Local)]
  public record LocalOnlyTestEvent([property: StreamId] Guid Id) : IEvent;

  #endregion

  #region Test Receptors

  /// <summary>
  /// Non-void receptor that returns an event.
  /// This mirrors JDNext's CreateTenantCommandHandler pattern.
  /// </summary>
  public class CascadeTestCommandHandler : IReceptor<CascadeTestCommand, CascadeTestEvent> {
    public ValueTask<CascadeTestEvent> HandleAsync(CascadeTestCommand command, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new CascadeTestEvent(command.Id));
    }
  }

  /// <summary>
  /// Local event tracker to verify local routing works.
  /// </summary>
  public static class LocalEventTracker {
    private static readonly List<object> _events = [];
    private static readonly Lock _lock = new();

    public static void Reset() {
      lock (_lock) {
        _events.Clear();
      }
    }

    public static void Track(object evt) {
      lock (_lock) {
        _events.Add(evt);
      }
    }

    public static int Count {
      get {
        lock (_lock) {
          return _events.Count;
        }
      }
    }
  }

  /// <summary>
  /// Receptor that tracks LocalOnlyTestEvent locally.
  /// </summary>
  public class LocalOnlyEventTracker : IReceptor<LocalOnlyTestEvent> {
    public ValueTask HandleAsync(LocalOnlyTestEvent message, CancellationToken cancellationToken = default) {
      LocalEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  #endregion

  #region Core Tests - The JDNext Scenario

  /// <summary>
  /// THE CRITICAL TEST: Void LocalInvokeAsync with a non-void receptor.
  /// This is the exact pattern used in JDNext UserService where events
  /// returned from CreateTenantCommandHandler weren't appearing in outbox.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task VoidLocalInvokeAsync_NonVoidReceptorReturnsEvent_CascadesToOutboxAsync() {
    // Arrange
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var command = new CascadeTestCommand(Guid.CreateVersion7());

    // Act - Use VOID LocalInvokeAsync (no generic type parameter)
    // This is how JDNext calls CreateTenantCommandHandler
    await dispatcher.LocalInvokeAsync(command);

    // Flush the strategy to ensure messages are written to database
    var strategy = serviceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    await strategy.FlushAsync(WorkBatchOptions.None);

    // Assert - Event should be in outbox table
    await using var dbContext = CreateDbContext();
    var outboxMessages = await dbContext.Outbox.ToListAsync();

    await Assert.That(outboxMessages).Count().IsGreaterThan(0)
      .Because("Event returned from non-void receptor should cascade to outbox via system default Outbox routing");

    // Verify it's our event
    var expectedType = typeof(CascadeTestEvent).AssemblyQualifiedName ?? throw new InvalidOperationException("AssemblyQualifiedName is null");
    var messageTypes = outboxMessages.Select(m => m.MessageType).ToList();
    await Assert.That(messageTypes).Contains(expectedType);
  }

  /// <summary>
  /// Control case: Generic LocalInvokeAsync with result capture.
  /// If void invoke fails but generic works, the bug is in the void path.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task GenericLocalInvokeAsync_ReceptorReturnsEvent_CascadesToOutboxAsync() {
    // Arrange
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var command = new CascadeTestCommand(Guid.CreateVersion7());

    // Act - Use GENERIC LocalInvokeAsync<TResult>
    var result = await dispatcher.LocalInvokeAsync<CascadeTestEvent>(command);

    // Verify we got the result
    await Assert.That(result).IsNotNull();
    await Assert.That(result.Id).IsEqualTo(command.Id);

    // Flush the strategy
    var strategy = serviceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    await strategy.FlushAsync(WorkBatchOptions.None);

    // Assert - Event should be in outbox table
    await using var dbContext = CreateDbContext();
    var outboxMessages = await dbContext.Outbox.ToListAsync();

    await Assert.That(outboxMessages).Count().IsGreaterThan(0)
      .Because("Event from generic LocalInvokeAsync should also cascade to outbox");
  }

  #endregion

  #region Diagnostic Tests

  /// <summary>
  /// Verifies that IWorkCoordinatorStrategy is properly registered and resolved.
  /// If this fails, the cascade-to-outbox flow can't work at all.
  /// </summary>
  [Test]
  public async Task Strategy_IsRegistered_CanBeResolvedAsync() {
    // Arrange
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    // Act
    var strategy = serviceProvider.GetService<IWorkCoordinatorStrategy>();

    // Assert
    await Assert.That(strategy).IsNotNull()
      .Because("IWorkCoordinatorStrategy must be registered for outbox routing to work");
    await Assert.That(strategy).IsTypeOf<ScopedWorkCoordinatorStrategy>()
      .Because("EFCore should use ScopedWorkCoordinatorStrategy");
  }

  /// <summary>
  /// Verifies that messages explicitly routed to Local don't go to outbox.
  /// This confirms routing logic is working correctly.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task LocalRouting_DoesNotCascadeToOutbox_GoesToLocalReceptorsOnlyAsync() {
    // Arrange
    LocalEventTracker.Reset();
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var evt = new LocalOnlyTestEvent(Guid.CreateVersion7());

    // Act - Publish a locally-routed event
    await dispatcher.LocalInvokeAsync(evt);

    // Assert - Should NOT be in outbox
    await using var dbContext = CreateDbContext();
    var outboxMessages = await dbContext.Outbox.ToListAsync();

    await Assert.That(outboxMessages).Count().IsEqualTo(0)
      .Because("[DefaultRouting(Local)] events should not go to outbox");

    // Assert - Should be tracked locally
    await Assert.That(LocalEventTracker.Count).IsEqualTo(1)
      .Because("Locally routed events should invoke local receptors");
  }

  /// <summary>
  /// Verifies that QueueOutboxMessage actually calls through to ProcessWorkBatchAsync.
  /// This pinpoints if the failure is in queueing vs flushing.
  /// </summary>
  [Test]
  public async Task Strategy_FlushAsync_WritesToDatabaseAsync() {
    // Arrange
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var strategy = serviceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    var testMessageId = Guid.CreateVersion7();
    var testMessage = CreateTestOutboxMessage(testMessageId, "test-topic", Guid.CreateVersion7(), isEvent: true);

    // Act - Queue directly and flush
    strategy.QueueOutboxMessage(testMessage);
    var workBatch = await strategy.FlushAsync(WorkBatchOptions.None);

    // Assert - Should have returned work
    await Assert.That(workBatch.OutboxWork).Count().IsGreaterThan(0)
      .Because("FlushAsync should return the newly stored message");

    // Assert - Message should be in database
    await using var dbContext = CreateDbContext();
    var outboxMessages = await dbContext.Outbox.ToListAsync();
    await Assert.That(outboxMessages).Count().IsGreaterThan(0);
  }

  #endregion

  #region AsSystem Security Context Tests - RED

  /// <summary>
  /// Test event for AsSystem() security propagation testing.
  /// </summary>
  public record AsSystemTestEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>
  /// **RED TEST**: Replicates the JDNext scenario where AsSystem().PublishAsync()
  /// stores an event to outbox, but when the event is retrieved and processed,
  /// MessageHopSecurityExtractor fails to extract the security context.
  ///
  /// This test verifies the full end-to-end flow:
  /// 1. dispatcher.AsSystem().ForTenant().PublishAsync() creates envelope with ScopeDelta
  /// 2. Event is stored in PostgreSQL outbox via work coordinator
  /// 3. Event is retrieved via ProcessWorkBatchAsync
  /// 4. MessageHopSecurityExtractor successfully extracts SYSTEM context from hops
  ///
  /// If this test FAILS, the bug is in the Dispatcher → Outbox → Extractor flow.
  /// </summary>
  /// <summary>
  /// This test verifies the full AsSystem() → Outbox → PostgreSQL → SecurityExtractor flow works.
  /// The test PASSES, confirming Whizbang correctly:
  /// 1. Stores ScopeDelta on message hops when AsSystem() is used
  /// 2. Persists scope through PostgreSQL JSONB
  /// 3. Allows MessageHopSecurityExtractor to extract SYSTEM context
  /// </summary>
  [Test]
  [NotInParallel]
  [Category("Integration")]
  public async Task AsSystem_PublishAsync_OutboxRoundTrip_SecurityExtractor_ExtractsSystemContextAsync() {
    // Arrange
    var services = await _createServicesWithEFCoreAsync();

    // Add IScopeContextAccessor for AsSystem() to work
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    // Add MessageHopSecurityExtractor for verification
    services.AddSingleton<ISecurityContextExtractor, MessageHopSecurityExtractor>();

    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var testEvent = new AsSystemTestEvent(Guid.CreateVersion7());

    // CRITICAL: Simulate being inside a message handler (like JDNext seeder)
    // This is what causes InitiatingContext to be set, which was overriding AsSystem()
    var handlerScope = new PerspectiveScope { UserId = "handler-user@example.com", TenantId = "handler-tenant" };
    var handlerExtraction = new SecurityExtraction {
      Scope = handlerScope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var handlerContext = new ImmutableScopeContext(handlerExtraction, shouldPropagate: true);
    var handlerMessageContext = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ScopeContext = handlerContext
    };
    ScopeContextAccessor.CurrentInitiatingContext = handlerMessageContext;

    try {
      // Act - Publish event with AsSystem().WithTenant() - like JDNext seeder does
      // NOTE: dispatcher.PublishAsync internally calls strategy.FlushAsync, so the message
      // is already stored in the database after this call completes.
      await dispatcher.AsSystem().ForTenant("system-tenant-123").PublishAsync(testEvent);

      // Query the database directly to get the stored message
      // (FlushAsync was already called by the dispatcher)
      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();

      // Assert - Outbox should have our message
      await Assert.That(outboxMessages).Count().IsGreaterThan(0)
        .Because("AsSystem().PublishAsync() should store event in outbox");

      // Find our event in the outbox
      var expectedType = typeof(AsSystemTestEvent).AssemblyQualifiedName ?? throw new InvalidOperationException("AssemblyQualifiedName is null");
      var ourMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);

      await Assert.That(ourMessage).IsNotNull()
        .Because("Our AsSystemTestEvent should be in the outbox");

      // OutboxMessageData already has strongly-typed Hops property
      var messageData = ourMessage!.MessageData;

      // THE CRITICAL ASSERTIONS - Verify ScopeDelta is on hops

      // 1. Message data should have hops
      await Assert.That(messageData.Hops).Count().IsGreaterThan(0)
        .Because("Message stored in outbox should have at least one hop");

      // 2. At least one hop should have Scope
      var hopWithScope = messageData.Hops.FirstOrDefault(h => h.Scope != null);
      await Assert.That(hopWithScope).IsNotNull()
        .Because("At least one hop should have ScopeDelta (from AsSystem())");

      // 3. Scope.Values should have ScopeProp.Scope key
      await Assert.That(hopWithScope!.Scope!.Values).IsNotNull()
        .Because("ScopeDelta.Values should not be null");

      // DIAGNOSTIC: Print what keys are actually in the dictionary after PostgreSQL round-trip
      Console.WriteLine("=== DIAGNOSTIC: ScopeDelta.Values Keys After PostgreSQL Round-Trip ===");
      foreach (var kvp in hopWithScope.Scope.Values!) {
        Console.WriteLine($"  Key: {kvp.Key} (int value: {(int)kvp.Key}), Value: {kvp.Value}");
      }
      Console.WriteLine($"  Dictionary Count: {hopWithScope.Scope.Values.Count}");
      Console.WriteLine($"  ContainsKey(ScopeProp.Scope): {hopWithScope.Scope.Values.ContainsKey(ScopeProp.Scope)}");
      Console.WriteLine("=== END DIAGNOSTIC ===");

      await Assert.That(hopWithScope.Scope.Values!.ContainsKey(ScopeProp.Scope)).IsTrue()
        .Because("ScopeDelta.Values should contain ScopeProp.Scope key");

      // Create an envelope to pass to the extractor (it needs IMessageEnvelope)
      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = messageData.MessageId,
        Payload = messageData.Payload,
        Hops = messageData.Hops
      };

      // 4. Now use MessageHopSecurityExtractor to extract security (the real test)
      var extractor = serviceProvider.GetRequiredService<ISecurityContextExtractor>();
      var securityOptions = new MessageSecurityOptions { AllowAnonymous = false };

      var extraction = await extractor.ExtractAsync(envelope, securityOptions);

      // THIS IS THE KEY ASSERTION - This is where JDNext fails
      await Assert.That(extraction).IsNotNull()
        .Because("MessageHopSecurityExtractor should successfully extract security from envelope hops. " +
                 "If this fails, the ScopeDelta is not being properly serialized/deserialized through PostgreSQL.");

      await Assert.That(extraction!.Scope?.UserId).IsEqualTo("SYSTEM")
        .Because("AsSystem() should set UserId to SYSTEM, not the handler's context (handler-user@example.com)");

      await Assert.That(extraction.Scope?.TenantId).IsEqualTo("system-tenant-123")
        .Because("WithTenant() should set TenantId to the specified tenant");

    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentInitiatingContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  /// <summary>
  /// Test command for cascaded event scenario.
  /// When handled, the receptor returns CascadedFromCommandEvent.
  /// </summary>
  public record AsSystemCascadeCommand([property: StreamId] Guid Id);

  /// <summary>
  /// Test event that is cascaded from AsSystemCascadeCommand handler.
  /// </summary>
  public record CascadedFromCommandEvent([property: StreamId] Guid Id) : IEvent;

  /// <summary>
  /// Receptor that handles AsSystemCascadeCommand and returns CascadedFromCommandEvent.
  /// The returned event should inherit the security context from AsSystem().
  /// </summary>
  public class AsSystemCascadeCommandHandler : IReceptor<AsSystemCascadeCommand, CascadedFromCommandEvent> {
    public ValueTask<CascadedFromCommandEvent> HandleAsync(AsSystemCascadeCommand command, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new CascadedFromCommandEvent(command.Id));
    }
  }

  /// <summary>
  /// **RED TEST**: Tests that CASCADED events from AsSystem().SendAsync() inherit the SYSTEM security context.
  ///
  /// This tests the actual JDNext scenario:
  /// 1. AsSystem().WithTenant().SendAsync(command) - sends command with explicit SYSTEM context
  /// 2. Receptor handles command and returns an event
  /// 3. Event is cascaded to outbox via CascadeToOutboxAsync
  /// 4. The cascaded event should have SYSTEM context, NOT the handler's context
  ///
  /// The key difference from AsSystem_PublishAsync test:
  /// - PublishAsync directly publishes an event
  /// - SendAsync triggers a receptor that cascades an event
  /// The cascade flow uses different code path (sourceEnvelope inheritance).
  /// </summary>
  [Test]
  [NotInParallel]
  [Category("RED")]
  public async Task AsSystem_SendAsync_CascadedEvent_InheritsSystemContextAsync() {
    // Arrange
    var services = await _createServicesWithEFCoreAsync();

    // Add IScopeContextAccessor for AsSystem() to work
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    // Register the receptor
    services.AddSingleton<IReceptor<AsSystemCascadeCommand, CascadedFromCommandEvent>, AsSystemCascadeCommandHandler>();

    // Add MessageHopSecurityExtractor for verification
    services.AddSingleton<ISecurityContextExtractor, MessageHopSecurityExtractor>();

    var serviceProvider = services.BuildServiceProvider();
    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

    var command = new AsSystemCascadeCommand(Guid.CreateVersion7());

    // CRITICAL: Simulate being inside a message handler (like JDNext seeder)
    // This sets InitiatingContext which was overriding AsSystem()
    var handlerScope = new PerspectiveScope { UserId = "handler-user@example.com", TenantId = "handler-tenant" };
    var handlerExtraction = new SecurityExtraction {
      Scope = handlerScope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var handlerContext = new ImmutableScopeContext(handlerExtraction, shouldPropagate: true);
    var handlerMessageContext = new MessageContext {
      CorrelationId = CorrelationId.New(),
      CausationId = MessageId.New(),
      ScopeContext = handlerContext
    };
    ScopeContextAccessor.CurrentInitiatingContext = handlerMessageContext;

    try {
      // Act - Send command with AsSystem().WithTenant() - this triggers the receptor
      // The receptor returns an event which should be cascaded with SYSTEM context
      await dispatcher.AsSystem().ForTenant("system-tenant-cascade").SendAsync(command);

      // Query the database directly to get the stored cascaded event
      await using var dbContext = CreateDbContext();
      var outboxMessages = await dbContext.Outbox.ToListAsync();

      // Assert - Outbox should have the cascaded event
      var expectedType = typeof(CascadedFromCommandEvent).AssemblyQualifiedName ?? throw new InvalidOperationException("AssemblyQualifiedName is null");
      var cascadedEvent = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);

      await Assert.That(cascadedEvent).IsNotNull()
        .Because("Cascaded event from receptor should be in the outbox");

      // Get the hops from the cascaded event
      var messageData = cascadedEvent!.MessageData;

      await Assert.That(messageData.Hops).Count().IsGreaterThan(0)
        .Because("Cascaded event should have at least one hop");

      var hopWithScope = messageData.Hops.FirstOrDefault(h => h.Scope != null);
      await Assert.That(hopWithScope).IsNotNull()
        .Because("At least one hop should have ScopeDelta (inherited from AsSystem())");

      await Assert.That(hopWithScope!.Scope!.Values).IsNotNull()
        .Because("ScopeDelta.Values should not be null");

      await Assert.That(hopWithScope.Scope.Values!.ContainsKey(ScopeProp.Scope)).IsTrue()
        .Because("ScopeDelta.Values should contain ScopeProp.Scope key");

      // Create an envelope to pass to the extractor
      var envelope = new MessageEnvelope<JsonElement> {
        MessageId = messageData.MessageId,
        Payload = messageData.Payload,
        Hops = messageData.Hops
      };

      // Use MessageHopSecurityExtractor to extract security
      var extractor = serviceProvider.GetRequiredService<ISecurityContextExtractor>();
      var securityOptions = new MessageSecurityOptions { AllowAnonymous = false };

      var extraction = await extractor.ExtractAsync(envelope, securityOptions);

      // THIS IS THE KEY ASSERTION - cascaded event should have SYSTEM context
      await Assert.That(extraction).IsNotNull()
        .Because("MessageHopSecurityExtractor should extract security from cascaded event hops");

      // CRITICAL: The cascaded event should have SYSTEM context, NOT handler's context
      await Assert.That(extraction!.Scope?.UserId).IsEqualTo("SYSTEM")
        .Because("Cascaded event from AsSystem().SendAsync() should have UserId=SYSTEM, " +
                 "NOT the handler's context (handler-user@example.com). " +
                 "If this fails, the cascade flow is not inheriting the explicit security context.");

      await Assert.That(extraction.Scope?.TenantId).IsEqualTo("system-tenant-cascade")
        .Because("Cascaded event should have the tenant specified in WithTenant()");

    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentInitiatingContext = null;
      await serviceProvider.DisposeAsync();
    }
  }

  #endregion

  #region Event Store Security Context Tests - RED

  /// <summary>
  /// Test event for event store security propagation testing.
  /// </summary>
  public record EventStoreSecurityTestEvent([property: StreamId] Guid StreamId) : IEvent;

  /// <summary>
  /// **RED TEST**: Replicates the JDNext PerspectiveWorker failure scenario.
  ///
  /// This tests the ACTUAL failing path in JDNext:
  /// 1. Event is stored to event store when ScopeContextAccessor.CurrentContext is null
  ///    (or not an ImmutableScopeContext with propagation enabled)
  /// 2. SecurityContextEventStoreDecorator calls GetSecurityFromAmbient() which returns null
  /// 3. Event is stored with NO ScopeDelta on hops
  /// 4. PerspectiveWorker reads event via GetEventsBetweenPolymorphicAsync
  /// 5. _establishSecurityContextAsync calls EstablishContextAsync
  /// 6. MessageHopSecurityExtractor returns null (no scope on hops)
  /// 7. DefaultMessageSecurityContextProvider throws SecurityContextRequiredException
  ///
  /// If this test PASSES, the bug is not reproducible.
  /// If this test FAILS with SecurityContextRequiredException, we've replicated the JDNext bug.
  /// </summary>
  [Test]
  [NotInParallel]
  [Category("RED")]
  public async Task EventStore_NoAmbientContext_PerspectiveWorker_ThrowsSecurityContextRequiredExceptionAsync() {
    // Arrange - Create EFCore event store directly (like EFCoreEventStoreTests)
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    // Set up services for security provider
    var services = new ServiceCollection();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    // Add message security with AllowAnonymous = false (this is the JDNext scenario)
    services.AddSingleton(new MessageSecurityOptions { AllowAnonymous = false, PropagateToOutgoingMessages = true });
    services.AddSingleton<ISecurityContextExtractor, MessageHopSecurityExtractor>();
    services.AddSingleton<IMessageSecurityContextProvider>(sp => {
      var extractors = sp.GetServices<ISecurityContextExtractor>();
      var options = sp.GetRequiredService<MessageSecurityOptions>();
      return new DefaultMessageSecurityContextProvider(extractors, [], options);
    });

    var serviceProvider = services.BuildServiceProvider();

    // CRITICAL: Ensure ScopeContextAccessor.CurrentContext is NULL
    // This simulates what happens when a receptor receives a message without security context
    ScopeContextAccessor.CurrentContext = null;

    var streamId = Guid.CreateVersion7();
    var testEvent = new EventStoreSecurityTestEvent(streamId);

    // Act Part 1: Store event to event store WITHOUT ambient security context
    // We're storing directly with Scope = null to simulate missing security context
    var eventEnvelope = new MessageEnvelope<EventStoreSecurityTestEvent> {
      MessageId = MessageId.New(),
      Payload = testEvent,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          ServiceInstance = ServiceInstanceInfo.Unknown,
          Timestamp = DateTimeOffset.UtcNow,
          Scope = null  // Explicitly no scope - simulating missing security context
        }
      ]
    };
    await eventStore.AppendAsync(streamId, eventEnvelope);

    // Act Part 2: Read event via GetEventsBetweenPolymorphicAsync (PerspectiveWorker path)
    var events = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: null,
      upToEventId: Guid.Empty,  // Read all events
      eventTypes: [typeof(EventStoreSecurityTestEvent)]);

    await Assert.That(events).Count().IsEqualTo(1)
      .Because("Event should be stored and retrievable");

    var retrievedEnvelope = events[0];

    // DIAGNOSTIC: Check if scope exists on hops
    var hopWithScope = retrievedEnvelope.Hops.FirstOrDefault(h => h.Scope != null);
    Console.WriteLine("=== DIAGNOSTIC: Event Store Security Context Test ===");
    Console.WriteLine($"Hops count: {retrievedEnvelope.Hops.Count}");
    foreach (var hop in retrievedEnvelope.Hops) {
      Console.WriteLine($"  Hop: Type={hop.Type}, Scope={hop.Scope?.ToString() ?? "NULL"}");
      if (hop.Scope?.Values != null) {
        foreach (var kvp in hop.Scope.Values) {
          Console.WriteLine($"    ScopeDelta.Values[{kvp.Key}] = {kvp.Value}");
        }
      }
    }
    Console.WriteLine($"Hop with scope: {(hopWithScope != null ? "FOUND" : "NOT FOUND")}");
    Console.WriteLine("=== END DIAGNOSTIC ===");

    // Act Part 3: Try to extract security context (PerspectiveWorker._establishSecurityContextAsync path)
    var securityProvider = serviceProvider.GetRequiredService<IMessageSecurityContextProvider>();

    // THIS IS WHERE JDNEXT FAILS
    // The extractor should throw SecurityContextRequiredException because:
    // 1. MessageHopSecurityExtractor finds no ScopeDelta on hops
    // 2. AllowAnonymous = false
    // 3. DefaultMessageSecurityContextProvider throws SecurityContextRequiredException
    try {
      var securityContext = await securityProvider.EstablishContextAsync(
        retrievedEnvelope,
        serviceProvider);

      // If we get here without exception, check what we got
      Console.WriteLine($"Security context established: {securityContext?.GetType().Name ?? "null"}");
      Console.WriteLine($"  UserId: {securityContext?.Scope?.UserId ?? "(null)"}");
      Console.WriteLine($"  TenantId: {securityContext?.Scope?.TenantId ?? "(null)"}");

      // The test should FAIL here - we expect an exception
      throw new InvalidOperationException(
        "Expected SecurityContextRequiredException but EstablishContextAsync succeeded. " +
        "This means the bug is not reproducible in this test setup. " +
        $"Got context with UserId={securityContext?.Scope?.UserId}, TenantId={securityContext?.Scope?.TenantId}");
    } catch (SecurityContextRequiredException ex) {
      // EXPECTED: This is the JDNext behavior - test passes because we reproduced the bug
      Console.WriteLine("=== REPRODUCED BUG ===");
      Console.WriteLine($"SecurityContextRequiredException: {ex.Message}");
      Console.WriteLine("=== END ===");

      // Now we need to FIX this - the exception should NOT be thrown
      // For the RED phase, we mark this as expected and will fix it
      // Test passes by not throwing
    }
  }

  /// <summary>
  /// **RED TEST**: Same scenario but with SecurityContextEventStoreDecorator in the chain.
  /// This tests whether the decorator properly propagates security context when ambient context IS set.
  /// </summary>
  [Test]
  [NotInParallel]
  [Category("RED")]
  public async Task EventStore_WithAmbientContext_PerspectiveWorker_ExtractsSecurityContextAsync() {
    // Arrange - Create EFCore event store with the SecurityContextEventStoreDecorator
    await using var context = CreateDbContext();
    var innerEventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    // Wrap with decorator that adds scope from ambient context
    var eventStore = new SecurityContextEventStoreDecorator(innerEventStore);

    // Set up services for security provider
    var services = new ServiceCollection();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();

    // Add message security with AllowAnonymous = false
    services.AddSingleton(new MessageSecurityOptions { AllowAnonymous = false, PropagateToOutgoingMessages = true });
    services.AddSingleton<ISecurityContextExtractor, MessageHopSecurityExtractor>();
    services.AddSingleton<IMessageSecurityContextProvider>(sp => {
      var extractors = sp.GetServices<ISecurityContextExtractor>();
      var options = sp.GetRequiredService<MessageSecurityOptions>();
      return new DefaultMessageSecurityContextProvider(extractors, [], options);
    });

    var serviceProvider = services.BuildServiceProvider();

    // CRITICAL: Set up ambient security context with propagation enabled
    var handlerScope = new PerspectiveScope { UserId = "test-user@example.com", TenantId = "test-tenant-123" };
    var handlerExtraction = new SecurityExtraction {
      Scope = handlerScope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    // shouldPropagate = true is CRITICAL for GetSecurityFromAmbient() to work
    var handlerContext = new ImmutableScopeContext(handlerExtraction, shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = handlerContext;

    try {
      var streamId = Guid.CreateVersion7();
      var testEvent = new EventStoreSecurityTestEvent(streamId);

      // Act Part 1: Store event - now GetSecurityFromAmbient() should return the context
      // Use the message-only overload which goes through SecurityContextEventStoreDecorator
      await eventStore.AppendAsync(streamId, testEvent);

      // Act Part 2: Read event via GetEventsBetweenPolymorphicAsync (PerspectiveWorker path)
      var events = await eventStore.GetEventsBetweenPolymorphicAsync(
        streamId,
        afterEventId: null,
        upToEventId: Guid.Empty,
        eventTypes: [typeof(EventStoreSecurityTestEvent)]);

      await Assert.That(events).Count().IsEqualTo(1)
        .Because("Event should be stored and retrievable");

      var retrievedEnvelope = events[0];

      // DIAGNOSTIC: Check if scope exists on hops
      Console.WriteLine("=== DIAGNOSTIC: Event Store WITH Ambient Context ===");
      Console.WriteLine($"Hops count: {retrievedEnvelope.Hops.Count}");
      foreach (var hop in retrievedEnvelope.Hops) {
        Console.WriteLine($"  Hop: Type={hop.Type}, Scope={hop.Scope?.ToString() ?? "NULL"}");
        if (hop.Scope?.Values != null) {
          foreach (var kvp in hop.Scope.Values) {
            Console.WriteLine($"    ScopeDelta.Values[{kvp.Key}] = {kvp.Value}");
          }
        }
      }
      Console.WriteLine("=== END DIAGNOSTIC ===");

      // Act Part 3: Extract security context
      var securityProvider = serviceProvider.GetRequiredService<IMessageSecurityContextProvider>();
      var securityContext = await securityProvider.EstablishContextAsync(
        retrievedEnvelope,
        serviceProvider);

      // Assert - This is the GREEN scenario: security context should be extracted successfully
      await Assert.That(securityContext).IsNotNull()
        .Because("Security context should be extracted from event hops when ambient context was set during append");

      await Assert.That(securityContext!.Scope?.UserId).IsEqualTo("test-user@example.com")
        .Because("UserId should match what was in the ambient context");

      await Assert.That(securityContext.Scope?.TenantId).IsEqualTo("test-tenant-123")
        .Because("TenantId should match what was in the ambient context");

    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
    }
  }

  #endregion

  /// <summary>
  /// **RED → GREEN TEST**: Tests the InitiatingContext shadowing bug fix.
  ///
  /// BEFORE FIX (RED): This test FAILS because:
  /// 1. ReceptorInvoker sets BOTH:
  ///    - accessor.Current = ImmutableScopeContext (with ShouldPropagate=true)
  ///    - accessor.InitiatingContext = messageContext (where messageContext.ScopeContext may be null)
  /// 2. ScopeContextAccessor.CurrentContext getter returns InitiatingContext.ScopeContext first
  /// 3. GetSecurityFromAmbient() checks for ImmutableScopeContext, gets null, returns null
  /// 4. SecurityContextEventStoreDecorator stores event with Scope=null on hops
  ///
  /// AFTER FIX (GREEN): This test PASSES because:
  /// 1. ScopeContextAccessor.CurrentContext getter now checks _current.Value first
  ///    if it's an ImmutableScopeContext with ShouldPropagate=true
  /// 2. GetSecurityFromAmbient() finds the ImmutableScopeContext
  /// 3. SecurityContextEventStoreDecorator stores event with proper ScopeDelta on hops
  /// </summary>
  [Test]
  [NotInParallel]
  [Category("RED")]
  public async Task InitiatingContext_DoesNotShadow_ImmutableScopeContextWithPropagationAsync() {
    // Arrange - Create EFCore event store with the SecurityContextEventStoreDecorator
    await using var context = CreateDbContext();
    var innerEventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);
    var eventStore = new SecurityContextEventStoreDecorator(innerEventStore);

    // Set up services for security provider
    var services = new ServiceCollection();
    services.AddSingleton<IScopeContextAccessor, ScopeContextAccessor>();
    services.AddSingleton(new MessageSecurityOptions { AllowAnonymous = false, PropagateToOutgoingMessages = true });
    services.AddSingleton<ISecurityContextExtractor, MessageHopSecurityExtractor>();
    services.AddSingleton<IMessageSecurityContextProvider>(sp => {
      var extractors = sp.GetServices<ISecurityContextExtractor>();
      var options = sp.GetRequiredService<MessageSecurityOptions>();
      return new DefaultMessageSecurityContextProvider(extractors, [], options);
    });

    var serviceProvider = services.BuildServiceProvider();

    // Set up the DUAL context scenario (this is what ReceptorInvoker does):
    // 1. Set _current to ImmutableScopeContext with ShouldPropagate=true
    var handlerScope = new PerspectiveScope { UserId = "cascade-user", TenantId = "cascade-tenant" };
    var handlerExtraction = new SecurityExtraction {
      Scope = handlerScope,
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test"
    };
    var immutableContext = new ImmutableScopeContext(handlerExtraction, shouldPropagate: true);
    ScopeContextAccessor.CurrentContext = immutableContext;  // Sets _current.Value

    // 2. ALSO set InitiatingContext (this is what shadows _current in the old code)
    // We create a minimal message context with a NON-NULL ScopeContext that is
    // NOT an ImmutableScopeContext (or has ShouldPropagate=false)
    // This simulates what happens when IMessageContext.ScopeContext is set but not to
    // the ImmutableScopeContext from EstablishContextAsync
    var wrongScope = new ScopeContext {
      Scope = new PerspectiveScope { UserId = "wrong-user", TenantId = "wrong-tenant" },
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>()
    };
    var messageContext = new MockMessageContext {
      MessageId = MessageId.New(),
      ScopeContext = wrongScope  // Non-null, non-ImmutableScopeContext shadows _current.Value!
    };
    ScopeContextAccessor.CurrentInitiatingContext = messageContext;

    try {
      var streamId = Guid.CreateVersion7();
      var testEvent = new EventStoreSecurityTestEvent(streamId);

      // DIAGNOSTIC: Check what GetSecurityFromAmbient returns
      var ambientSecurity = Whizbang.Core.Observability.CascadeContext.GetSecurityFromAmbient();
      Console.WriteLine("=== DIAGNOSTIC: InitiatingContext Shadowing Test ===");
      Console.WriteLine($"ScopeContextAccessor.CurrentContext type: {ScopeContextAccessor.CurrentContext?.GetType().Name ?? "null"}");
      Console.WriteLine($"ScopeContextAccessor.CurrentInitiatingContext: {ScopeContextAccessor.CurrentInitiatingContext != null}");
      Console.WriteLine($"GetSecurityFromAmbient() returned: {(ambientSecurity != null ? $"UserId={ambientSecurity.UserId}, TenantId={ambientSecurity.TenantId}" : "null")}");

      // Act: Store event - this is where the bug would manifest
      // OLD CODE: GetSecurityFromAmbient() returns null because InitiatingContext.ScopeContext is checked first
      // NEW CODE: GetSecurityFromAmbient() returns the ImmutableScopeContext because _current is checked first
      await eventStore.AppendAsync(streamId, testEvent);

      // Read event back
      var events = await eventStore.GetEventsBetweenPolymorphicAsync(
        streamId,
        afterEventId: null,
        upToEventId: Guid.Empty,
        eventTypes: [typeof(EventStoreSecurityTestEvent)]);

      await Assert.That(events).Count().IsEqualTo(1)
        .Because("Event should be stored and retrievable");

      var retrievedEnvelope = events[0];

      // DIAGNOSTIC: Check stored hops
      Console.WriteLine($"Stored hops count: {retrievedEnvelope.Hops.Count}");
      foreach (var hop in retrievedEnvelope.Hops) {
        Console.WriteLine($"  Hop: Scope={hop.Scope?.ToString() ?? "NULL"}");
        if (hop.Scope?.Values != null) {
          foreach (var kvp in hop.Scope.Values) {
            Console.WriteLine($"    ScopeDelta.Values[{kvp.Key}] = {kvp.Value}");
          }
        }
      }
      Console.WriteLine("=== END DIAGNOSTIC ===");

      // Assert: Verify scope IS on the hops (this would FAIL before the fix)
      var hopWithScope = retrievedEnvelope.Hops.FirstOrDefault(h => h.Scope != null);
      await Assert.That(hopWithScope).IsNotNull()
        .Because("Event should have ScopeDelta on hops even when InitiatingContext is set");

      // Extract security context to verify it works end-to-end
      var securityProvider = serviceProvider.GetRequiredService<IMessageSecurityContextProvider>();
      var securityContext = await securityProvider.EstablishContextAsync(
        retrievedEnvelope,
        serviceProvider);

      await Assert.That(securityContext).IsNotNull()
        .Because("Security context should be extractable from stored event");

      await Assert.That(securityContext!.Scope?.UserId).IsEqualTo("cascade-user")
        .Because("UserId should match the ImmutableScopeContext, not InitiatingContext");

      await Assert.That(securityContext.Scope?.TenantId).IsEqualTo("cascade-tenant")
        .Because("TenantId should match the ImmutableScopeContext, not InitiatingContext");

    } finally {
      // Cleanup
      ScopeContextAccessor.CurrentContext = null;
      ScopeContextAccessor.CurrentInitiatingContext = null;
    }
  }

  /// <summary>
  /// Mock message context for testing InitiatingContext shadowing.
  /// </summary>
  private sealed record MockMessageContext : IMessageContext {
    public required MessageId MessageId { get; init; }
    public CorrelationId CorrelationId { get; init; } = CorrelationId.New();
    public MessageId CausationId { get; init; } = MessageId.New();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? UserId { get; init; }
    public string? TenantId { get; init; }
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
    public IScopeContext? ScopeContext { get; init; }
    public ICallerInfo? CallerInfo => null;
  }

  #region Raw JSONB Round-Trip Tests

  /// <summary>
  /// Tests the raw JSONB round-trip of ScopeDelta.Values dictionary.
  /// This test directly verifies what happens when Dictionary&lt;ScopeProp, JsonElement&gt;
  /// is stored and retrieved through PostgreSQL JSONB.
  /// </summary>
  [Test]
  [NotInParallel]
  [Category("RED")]
  public async Task ScopeDelta_RawJsonbRoundTrip_EnumKeysPreservedAsync() {
    // Arrange - ensure base setup
    await base.SetupAsync();

    var messageId = Guid.CreateVersion7();
    var streamId = Guid.CreateVersion7();
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();

    // Create a ScopeDelta with Dictionary<ScopeProp, JsonElement>
    var scopeData = new PerspectiveScope {
      TenantId = "test-tenant-roundtrip",
      UserId = "SYSTEM-ROUNDTRIP"
    };
    var scopeElement = JsonSerializer.SerializeToElement(scopeData, jsonOptions);

    var scopeDelta = new ScopeDelta {
      Values = new Dictionary<ScopeProp, JsonElement> {
        [ScopeProp.Scope] = scopeElement
      }
    };

    // Create MessageHop with the ScopeDelta
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = new ServiceInstanceInfo {
        ServiceName = "TestService",
        InstanceId = Guid.CreateVersion7(),
        HostName = "test-host",
        ProcessId = System.Environment.ProcessId
      },
      Timestamp = DateTimeOffset.UtcNow,
      Topic = "test-topic",
      StreamId = streamId.ToString(),
      ExecutionStrategy = "test",
      Scope = scopeDelta
    };

    // Create envelope with the hop
    var envelope = new MessageEnvelope<JsonElement> {
      MessageId = MessageId.From(messageId),
      Payload = JsonDocument.Parse("{}").RootElement,
      Hops = [hop]
    };

    // Create outbox message using the helper that sets all required properties
    var outboxMessage = CreateTestOutboxMessage(messageId, "test-destination", streamId, isEvent: true);
    // Overwrite the envelope with our custom one that has ScopeDelta
    outboxMessage = outboxMessage with {
      Envelope = envelope
    };

    // Act - Store via work coordinator
    await using var dbContext = CreateDbContext();
    var coordinator = new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, jsonOptions);

    var instanceId = Guid.CreateVersion7();
    var request = new ProcessWorkBatchRequest {
      InstanceId = instanceId,
      ServiceName = "TestService",
      HostName = "test-host",
      ProcessId = Environment.ProcessId,
      Metadata = null,
      OutboxCompletions = [],
      OutboxFailures = [],
      InboxCompletions = [],
      InboxFailures = [],
      ReceptorCompletions = [],
      ReceptorFailures = [],
      PerspectiveCompletions = [],
      PerspectiveEventCompletions = [],
      PerspectiveFailures = [],
      NewOutboxMessages = [outboxMessage],
      NewInboxMessages = [],
      RenewOutboxLeaseIds = [],
      RenewInboxLeaseIds = [],
      Flags = WorkBatchOptions.DebugMode
    };

    var workBatch = await coordinator.ProcessWorkBatchAsync(request);

    // Now read back the raw event_data column via ADO.NET to see the actual JSON
    string? rawJson = null;
    var connection = dbContext.Database.GetDbConnection();
    await connection.OpenAsync();
    await using (var cmd = connection.CreateCommand()) {
      cmd.CommandText = $"SELECT event_data::text FROM wh_outbox WHERE message_id = '{messageId}'";
      rawJson = await cmd.ExecuteScalarAsync() as string;
    }

    // DIAGNOSTIC OUTPUT
    Console.WriteLine("=== RAW JSONB FROM POSTGRESQL ===");
    Console.WriteLine(rawJson);
    Console.WriteLine("=== END RAW JSONB ===");

    // Parse the raw JSON to see the actual structure
    if (rawJson != null) {
      using var doc = JsonDocument.Parse(rawJson);
      var root = doc.RootElement;

      Console.WriteLine("\n=== PARSED JSON STRUCTURE ===");
      if (root.TryGetProperty("h", out var hops) || root.TryGetProperty("Hops", out hops)) {
        Console.WriteLine($"Found hops array with {hops.GetArrayLength()} elements");
        var firstHop = hops.EnumerateArray().FirstOrDefault();
        if (firstHop.TryGetProperty("sc", out var sc) || firstHop.TryGetProperty("Scope", out sc)) {
          Console.WriteLine($"Found scope on hop: {sc}");
          if (sc.TryGetProperty("v", out var v) || sc.TryGetProperty("Values", out v)) {
            Console.WriteLine($"Found Values: {v}");
            Console.WriteLine($"Values kind: {v.ValueKind}");
            if (v.ValueKind == JsonValueKind.Object) {
              foreach (var prop in v.EnumerateObject()) {
                Console.WriteLine($"  Key: '{prop.Name}' Value: {prop.Value}");
              }
            }
          }
        }
      }
      Console.WriteLine("=== END STRUCTURE ===");
    }

    // Assert - verify the work batch contains our message
    await Assert.That(workBatch.OutboxWork).Count().IsGreaterThan(0)
      .Because("Work batch should contain our stored message");

    var retrievedWork = workBatch.OutboxWork.First();
    var retrievedHop = retrievedWork.Envelope.Hops.FirstOrDefault(h => h.Scope != null);

    await Assert.That(retrievedHop).IsNotNull()
      .Because("Retrieved message should have hop with scope");

    await Assert.That(retrievedHop!.Scope!.Values).IsNotNull()
      .Because("ScopeDelta.Values should not be null after round-trip");

    // THE CRITICAL ASSERTION - Do the enum keys survive?
    var hasKey = retrievedHop.Scope.Values!.ContainsKey(ScopeProp.Scope);

    Console.WriteLine("\n=== KEY CHECK ===");
    Console.WriteLine($"ContainsKey(ScopeProp.Scope): {hasKey}");
    Console.WriteLine($"Dictionary count: {retrievedHop.Scope.Values.Count}");
    Console.WriteLine("Actual keys in dictionary:");
    foreach (var kvp in retrievedHop.Scope.Values) {
      Console.WriteLine($"  {kvp.Key} (int: {(int)kvp.Key}): {kvp.Value}");
    }
    Console.WriteLine("=== END KEY CHECK ===");

    await Assert.That(hasKey).IsTrue()
      .Because("ScopeProp.Scope key should survive PostgreSQL JSONB round-trip. " +
               "If this fails, the enum keys are being serialized differently than expected.");
  }

  #endregion

  #region Helper Methods

  /// <summary>
  /// Creates a service collection with all dependencies for cascade-to-outbox testing.
  /// This mirrors how JDNext services configure their DI.
  /// </summary>
  private async Task<ServiceCollection> _createServicesWithEFCoreAsync() {
    // Ensure base setup has run
    await base.SetupAsync();

    var services = new ServiceCollection();

    // Register service instance provider
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));

    // Register DbContext with our test options
    services.AddScoped(_ => CreateDbContext());

    // Register JSON serialization
    var jsonOptions = JsonContextRegistry.CreateCombinedOptions();
    services.AddSingleton(jsonOptions);

    // Register envelope serializer
    services.AddSingleton<IEnvelopeSerializer, EnvelopeSerializer>();

    // Register logging
    services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

    // Register EFCore work coordinator
    services.AddScoped<IWorkCoordinator>(sp => {
      var dbContext = sp.GetRequiredService<WorkCoordinationDbContext>();
      return new EFCoreWorkCoordinator<WorkCoordinationDbContext>(dbContext, jsonOptions);
    });

    // Register scoped strategy
    services.AddScoped<IWorkCoordinatorStrategy>(sp => {
      var coordinator = sp.GetRequiredService<IWorkCoordinator>();
      var instanceProvider = sp.GetRequiredService<IServiceInstanceProvider>();
      var logger = sp.GetService<ILogger<ScopedWorkCoordinatorStrategy>>();
      var options = new WorkCoordinatorOptions {
        LeaseSeconds = 30,
        StaleThresholdSeconds = 300,
        PartitionCount = 4
      };
      return new ScopedWorkCoordinatorStrategy(
        coordinator,
        instanceProvider,
        workChannelWriter: null,
        options,
        logger
      );
    });

    // Register receptors and dispatcher (will pick up test receptors from this assembly)
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    return services;
  }

  #endregion
}
