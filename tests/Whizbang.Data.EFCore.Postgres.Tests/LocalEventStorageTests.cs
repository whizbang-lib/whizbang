using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Integration tests for local event storage functionality.
/// These tests verify that events with null destination (event-store-only mode)
/// are properly stored in the event store while transport is bypassed.
/// </summary>
/// <remarks>
/// <tests>src/Whizbang.Core/Dispatch/Route.cs:Local,EventStoreOnly,LocalNoPersist</tests>
/// <tests>src/Whizbang.Core/Workers/TransportPublishStrategy.cs:PublishAsync null destination bypass</tests>
///
/// Key insight: Cascade routing happens when events are RETURNED from receptors,
/// not when events are directly dispatched. These tests use command handlers
/// that return events with different routing modes to verify the cascade flow.
/// </remarks>
public class LocalEventStorageTests : EFCoreTestBase {
  #region Test Messages

  /// <summary>
  /// Test command that triggers LocalStoredEvent.
  /// </summary>
  public record TriggerLocalStoredCommand([property: StreamKey] Guid StreamId, string Data);

  /// <summary>
  /// Test command that triggers LocalNoPersistEvent.
  /// </summary>
  public record TriggerLocalNoPersistCommand([property: StreamKey] Guid StreamId, string Data);

  /// <summary>
  /// Test command that triggers EventStoreOnlyEvent.
  /// </summary>
  public record TriggerEventStoreOnlyCommand([property: StreamKey] Guid StreamId, string Data);

  /// <summary>
  /// Test command that triggers OutboxRoutedEvent.
  /// </summary>
  public record TriggerOutboxCommand([property: StreamKey] Guid StreamId, string Data);

  /// <summary>
  /// Test event routed to event store only (Route.Local with new behavior).
  /// Uses default LocalDispatch | EventStore mode.
  /// </summary>
  [DefaultRouting(DispatchMode.Local)]
  public record LocalStoredEvent([property: StreamKey] Guid StreamId, string Data) : IEvent;

  /// <summary>
  /// Test event with no persistence (Route.LocalNoPersist).
  /// Uses LocalDispatch mode only - no event store, no transport.
  /// </summary>
  [DefaultRouting(DispatchMode.LocalNoPersist)]
  public record LocalNoPersistEvent([property: StreamKey] Guid StreamId, string Data) : IEvent;

  /// <summary>
  /// Test event routed to event store only without local dispatch.
  /// Uses EventStoreOnly mode - stores to event store, no local receptors, no transport.
  /// </summary>
  [DefaultRouting(DispatchMode.EventStoreOnly)]
  public record EventStoreOnlyEvent([property: StreamKey] Guid StreamId, string Data) : IEvent;

  /// <summary>
  /// Test event routed to outbox with transport.
  /// Uses Outbox mode - standard outbox flow with transport.
  /// </summary>
  [DefaultRouting(DispatchMode.Outbox)]
  public record OutboxRoutedEvent([property: StreamKey] Guid StreamId, string Data) : IEvent;

  #endregion

  #region Event Tracking

  /// <summary>
  /// Tracker to verify local receptors are invoked.
  /// </summary>
  public static class LocalEventTracker {
    private static readonly List<object> _events = [];
    private static readonly object _lock = new();

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

    public static List<object> GetEvents() {
      lock (_lock) {
        return [.. _events];
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
  /// Receptor to track LocalStoredEvent delivery.
  /// </summary>
  public class LocalStoredEventReceptor : IReceptor<LocalStoredEvent> {
    public ValueTask HandleAsync(LocalStoredEvent message, CancellationToken cancellationToken = default) {
      LocalEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Receptor to track LocalNoPersistEvent delivery.
  /// </summary>
  public class LocalNoPersistEventReceptor : IReceptor<LocalNoPersistEvent> {
    public ValueTask HandleAsync(LocalNoPersistEvent message, CancellationToken cancellationToken = default) {
      LocalEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Receptor to track EventStoreOnlyEvent - should NOT be called for EventStoreOnly routing.
  /// </summary>
  public class EventStoreOnlyEventReceptor : IReceptor<EventStoreOnlyEvent> {
    public ValueTask HandleAsync(EventStoreOnlyEvent message, CancellationToken cancellationToken = default) {
      LocalEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  #endregion

  #region Command Handlers - Return Events with Different Routing

  /// <summary>
  /// Handler that returns LocalStoredEvent (Local routing = LocalDispatch | EventStore).
  /// </summary>
  public class TriggerLocalStoredCommandHandler : IReceptor<TriggerLocalStoredCommand, LocalStoredEvent> {
    public ValueTask<LocalStoredEvent> HandleAsync(TriggerLocalStoredCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new LocalStoredEvent(message.StreamId, message.Data));
    }
  }

  /// <summary>
  /// Handler that returns LocalNoPersistEvent (LocalNoPersist routing = LocalDispatch only).
  /// </summary>
  public class TriggerLocalNoPersistCommandHandler : IReceptor<TriggerLocalNoPersistCommand, LocalNoPersistEvent> {
    public ValueTask<LocalNoPersistEvent> HandleAsync(TriggerLocalNoPersistCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new LocalNoPersistEvent(message.StreamId, message.Data));
    }
  }

  /// <summary>
  /// Handler that returns EventStoreOnlyEvent (EventStoreOnly routing = storage only, no local dispatch).
  /// </summary>
  public class TriggerEventStoreOnlyCommandHandler : IReceptor<TriggerEventStoreOnlyCommand, EventStoreOnlyEvent> {
    public ValueTask<EventStoreOnlyEvent> HandleAsync(TriggerEventStoreOnlyCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new EventStoreOnlyEvent(message.StreamId, message.Data));
    }
  }

  /// <summary>
  /// Handler that returns OutboxRoutedEvent (Outbox routing = standard outbox flow).
  /// </summary>
  public class TriggerOutboxCommandHandler : IReceptor<TriggerOutboxCommand, OutboxRoutedEvent> {
    public ValueTask<OutboxRoutedEvent> HandleAsync(TriggerOutboxCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new OutboxRoutedEvent(message.StreamId, message.Data));
    }
  }

  #endregion

  #region Route.Local Tests - Event Store + Local Dispatch

  /// <summary>
  /// Route.Local should invoke local receptors when cascaded from command handler.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task RouteLocal_CascadedEvent_InvokesLocalReceptorsAsync() {
    // Arrange
    LocalEventTracker.Reset();
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var command = new TriggerLocalStoredCommand(Guid.CreateVersion7(), "Test data");

    // Act - Command handler returns LocalStoredEvent with [DefaultRouting(DispatchMode.Local)]
    await dispatcher.LocalInvokeAsync(command);

    // Assert - Local receptor should have been invoked for the cascaded event
    await Assert.That(LocalEventTracker.Count).IsEqualTo(1)
      .Because("Route.Local should invoke local receptors for cascaded events");
    await Assert.That(LocalEventTracker.GetEvents()[0]).IsTypeOf<LocalStoredEvent>();
  }

  /// <summary>
  /// Route.Local should store event to outbox with null destination when cascaded.
  /// The null destination signals event-store-only mode (transport bypass).
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task RouteLocal_CascadedEvent_StoredToOutboxWithNullDestinationAsync() {
    // Arrange
    LocalEventTracker.Reset();
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var command = new TriggerLocalStoredCommand(Guid.CreateVersion7(), "Test data for storage");

    // Act - Command handler returns LocalStoredEvent with [DefaultRouting(DispatchMode.Local)]
    await dispatcher.LocalInvokeAsync(command);

    // Flush the strategy to write to database
    var strategy = serviceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert - Event should be in outbox with null destination
    await using var dbContext = CreateDbContext();
    var outboxMessages = await dbContext.Outbox.ToListAsync();

    await Assert.That(outboxMessages).Count().IsGreaterThan(0)
      .Because("Route.Local cascaded events should be stored to outbox for event store processing");

    var expectedType = typeof(LocalStoredEvent).AssemblyQualifiedName;
    var matchingMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
    await Assert.That(matchingMessage).IsNotNull()
      .Because("LocalStoredEvent should be in outbox");

    // Destination should be null for event-store-only mode
    await Assert.That(matchingMessage!.Destination).IsNull()
      .Because("Route.Local events should have null destination to bypass transport");
  }

  #endregion

  #region Route.LocalNoPersist Tests - Local Only, No Storage

  /// <summary>
  /// Route.LocalNoPersist should invoke local receptors when cascaded.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task RouteLocalNoPersist_CascadedEvent_InvokesLocalReceptorsAsync() {
    // Arrange
    LocalEventTracker.Reset();
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var command = new TriggerLocalNoPersistCommand(Guid.CreateVersion7(), "Test data");

    // Act - Command handler returns LocalNoPersistEvent with [DefaultRouting(DispatchMode.LocalNoPersist)]
    await dispatcher.LocalInvokeAsync(command);

    // Assert - Local receptor should have been invoked
    await Assert.That(LocalEventTracker.Count).IsEqualTo(1)
      .Because("Route.LocalNoPersist should invoke local receptors for cascaded events");
  }

  /// <summary>
  /// Route.LocalNoPersist should NOT store event to outbox when cascaded.
  /// This is the old Route.Local behavior - no persistence.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task RouteLocalNoPersist_CascadedEvent_NotStoredToOutboxAsync() {
    // Arrange
    LocalEventTracker.Reset();
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var command = new TriggerLocalNoPersistCommand(Guid.CreateVersion7(), "Test data - should not persist");

    // Act - Command handler returns LocalNoPersistEvent with [DefaultRouting(DispatchMode.LocalNoPersist)]
    await dispatcher.LocalInvokeAsync(command);

    // Flush the strategy
    var strategy = serviceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert - Event should NOT be in outbox
    await using var dbContext = CreateDbContext();
    var outboxMessages = await dbContext.Outbox.ToListAsync();

    var expectedType = typeof(LocalNoPersistEvent).AssemblyQualifiedName;
    var matchingMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
    await Assert.That(matchingMessage).IsNull()
      .Because("Route.LocalNoPersist events should NOT be stored to outbox");
  }

  #endregion

  #region Route.EventStoreOnly Tests - Storage Only, No Local Dispatch

  /// <summary>
  /// Route.EventStoreOnly should NOT invoke local receptors when cascaded.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task RouteEventStoreOnly_CascadedEvent_DoesNotInvokeLocalReceptorsAsync() {
    // Arrange
    LocalEventTracker.Reset();
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var command = new TriggerEventStoreOnlyCommand(Guid.CreateVersion7(), "Test data");

    // Act - Command handler returns EventStoreOnlyEvent with [DefaultRouting(DispatchMode.EventStoreOnly)]
    await dispatcher.LocalInvokeAsync(command);

    // Assert - Local receptor should NOT have been invoked for EventStoreOnly routing
    await Assert.That(LocalEventTracker.Count).IsEqualTo(0)
      .Because("Route.EventStoreOnly should NOT invoke local receptors for cascaded events");
  }

  /// <summary>
  /// Route.EventStoreOnly should store event to outbox with null destination when cascaded.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task RouteEventStoreOnly_CascadedEvent_StoredToOutboxWithNullDestinationAsync() {
    // Arrange
    LocalEventTracker.Reset();
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var command = new TriggerEventStoreOnlyCommand(Guid.CreateVersion7(), "Test data for storage only");

    // Act - Command handler returns EventStoreOnlyEvent with [DefaultRouting(DispatchMode.EventStoreOnly)]
    await dispatcher.LocalInvokeAsync(command);

    // Flush the strategy
    var strategy = serviceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert - Event should be in outbox with null destination
    await using var dbContext = CreateDbContext();
    var outboxMessages = await dbContext.Outbox.ToListAsync();

    var expectedType = typeof(EventStoreOnlyEvent).AssemblyQualifiedName;
    var matchingMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
    await Assert.That(matchingMessage).IsNotNull()
      .Because("Route.EventStoreOnly events should be stored to outbox");

    await Assert.That(matchingMessage!.Destination).IsNull()
      .Because("Route.EventStoreOnly events should have null destination to bypass transport");
  }

  #endregion

  #region Route.Outbox Tests - Standard Transport Flow

  /// <summary>
  /// Route.Outbox should store event to outbox with valid destination when cascaded.
  /// This verifies the standard outbox flow still works correctly.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task RouteOutbox_CascadedEvent_StoredToOutboxWithDestinationAsync() {
    // Arrange
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var command = new TriggerOutboxCommand(Guid.CreateVersion7(), "Test data for outbox");

    // Act - Command handler returns OutboxRoutedEvent with [DefaultRouting(DispatchMode.Outbox)]
    await dispatcher.LocalInvokeAsync(command);

    // Flush the strategy
    var strategy = serviceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert - Event should be in outbox with valid destination
    await using var dbContext = CreateDbContext();
    var outboxMessages = await dbContext.Outbox.ToListAsync();

    var expectedType = typeof(OutboxRoutedEvent).AssemblyQualifiedName;
    var matchingMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
    await Assert.That(matchingMessage).IsNotNull()
      .Because("Route.Outbox events should be stored to outbox");

    await Assert.That(matchingMessage!.Destination).IsNotNull()
      .Because("Route.Outbox events should have a valid destination for transport");
  }

  #endregion

  #region StreamId Tests

  /// <summary>
  /// Events stored via Route.Local should have a valid StreamId when cascaded.
  /// The StreamId is used for event store ordering and partitioning.
  /// Note: StreamId extraction from [StreamKey] requires top-level types for source generators.
  /// Nested test types fall back to MessageId as StreamId, which is still valid for partitioning.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task RouteLocal_CascadedEvent_HasStreamIdSetAsync() {
    // Arrange
    LocalEventTracker.Reset();
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var streamId = Guid.CreateVersion7();
    var command = new TriggerLocalStoredCommand(streamId, "Test data");

    // Act
    await dispatcher.LocalInvokeAsync(command);

    // Flush the strategy
    var strategy = serviceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert - Event should have a StreamId set (either from [StreamKey] or MessageId fallback)
    await using var dbContext = CreateDbContext();
    var outboxMessages = await dbContext.Outbox.ToListAsync();

    var expectedType = typeof(LocalStoredEvent).AssemblyQualifiedName;
    var matchingMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
    await Assert.That(matchingMessage).IsNotNull()
      .Because("Event should be stored to outbox");
    await Assert.That(matchingMessage!.StreamId).IsNotNull()
      .Because("Events should have a StreamId set for event store ordering (fallback to MessageId for nested test types)");
  }

  /// <summary>
  /// Events stored via Route.EventStoreOnly should have a valid StreamId when cascaded.
  /// Note: StreamId extraction from [StreamKey] requires top-level types for source generators.
  /// Nested test types fall back to MessageId as StreamId, which is still valid for partitioning.
  /// </summary>
  [Test]
  [NotInParallel]
  public async Task RouteEventStoreOnly_CascadedEvent_HasStreamIdSetAsync() {
    // Arrange
    LocalEventTracker.Reset();
    var services = await _createServicesWithEFCoreAsync();
    var serviceProvider = services.BuildServiceProvider();

    var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();
    var streamId = Guid.CreateVersion7();
    var command = new TriggerEventStoreOnlyCommand(streamId, "Test data");

    // Act
    await dispatcher.LocalInvokeAsync(command);

    // Flush the strategy
    var strategy = serviceProvider.GetRequiredService<IWorkCoordinatorStrategy>();
    await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert - Event should have a StreamId set (either from [StreamKey] or MessageId fallback)
    await using var dbContext = CreateDbContext();
    var outboxMessages = await dbContext.Outbox.ToListAsync();

    var expectedType = typeof(EventStoreOnlyEvent).AssemblyQualifiedName;
    var matchingMessage = outboxMessages.FirstOrDefault(m => m.MessageType == expectedType);
    await Assert.That(matchingMessage).IsNotNull()
      .Because("Event should be stored to outbox");
    await Assert.That(matchingMessage!.StreamId).IsNotNull()
      .Because("Events should have a StreamId set for event store ordering (fallback to MessageId for nested test types)");
  }

  #endregion

  #region Helper Methods

  /// <summary>
  /// Creates a service collection with all dependencies for local event storage testing.
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

    // Register receptors and dispatcher
    services.AddReceptors();
    services.AddWhizbangDispatcher();

    return services;
  }

  #endregion
}
