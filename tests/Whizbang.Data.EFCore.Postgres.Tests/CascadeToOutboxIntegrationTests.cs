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
  [DefaultRouting(DispatchMode.Local)]
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
    await strategy.FlushAsync(WorkBatchFlags.None);

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
    await strategy.FlushAsync(WorkBatchFlags.None);

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
    var workBatch = await strategy.FlushAsync(WorkBatchFlags.None);

    // Assert - Should have returned work
    await Assert.That(workBatch.OutboxWork).Count().IsGreaterThan(0)
      .Because("FlushAsync should return the newly stored message");

    // Assert - Message should be in database
    await using var dbContext = CreateDbContext();
    var outboxMessages = await dbContext.Outbox.ToListAsync();
    await Assert.That(outboxMessages).Count().IsGreaterThan(0);
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
