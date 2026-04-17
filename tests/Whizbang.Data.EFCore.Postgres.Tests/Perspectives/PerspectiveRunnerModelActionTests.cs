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
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Integration tests that run events through the generated ActionTestPerspectiveRunner
/// and verify database state. Tests all 4 ModelAction outcomes: None (create/update), Delete, Purge.
/// </summary>
public class PerspectiveRunnerModelActionTests : EFCoreTestBase {

  private static async Task<IPerspectiveRunner> CreateRunnerAsync(
      InMemoryEventStore eventStore,
      EFCorePostgresPerspectiveStore<ActionTestModel> perspectiveStore) {

    var services = new ServiceCollection();
    services.AddTransient<ActionTestPerspective>();
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    await Task.CompletedTask;

    // Construct the generated runner via reflection to avoid compile-time dependency
    // on the source-generated type. dotnet format on CI runs its own compilation pass
    // where source generators don't produce output, causing CS0246.
    var runnerType = typeof(PerspectiveRunnerModelActionTests).Assembly.GetTypes()
        .Single(t => t.Name == "ActionTestPerspectiveRunner");

    // Create ILogger<ActionTestPerspectiveRunner> via the generic CreateLogger<T> method
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var createLoggerMethod = typeof(LoggerFactoryExtensions)
        .GetMethods()
        .Single(m => m.Name == "CreateLogger" && m.IsGenericMethod)
        .MakeGenericMethod(runnerType);
    var logger = createLoggerMethod.Invoke(null, [loggerFactory])!;

    var ctor = runnerType.GetConstructors().Single();
    return (IPerspectiveRunner)ctor.Invoke([
      sp,
      logger,
      (IEventStore)eventStore,
      (IPerspectiveStore<ActionTestModel>)perspectiveStore,
      sp.GetRequiredService<IServiceScopeFactory>(),
      null, // tracingOptions
      null, // snapshotStore
      null  // snapshotOptions
    ]);
  }

  private static async Task AppendEventAsync<TEvent>(InMemoryEventStore eventStore, Guid streamId, TEvent payload)
      where TEvent : IEvent {
    var envelope = new MessageEnvelope<TEvent> {
      MessageId = MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    };
    await eventStore.AppendAsync(streamId, envelope);
  }

  [Test]
  public async Task RunAsync_WithCreatedEvent_InsertsRowAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "Test Item",
      Value = 42
    });

    await using var storeContext = CreateDbContext();
    var perspectiveStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, "action_test");
    var runner = await CreateRunnerAsync(eventStore, perspectiveStore);

    // Act
    await runner.RunAsync(streamId, "action_test", null, CancellationToken.None);

    // Assert
    await using var verifyContext = CreateDbContext();
    var row = await verifyContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Test Item");
    await Assert.That(row.Data.Value).IsEqualTo(42);
    await Assert.That(row.Data.DeletedAt).IsNull();
  }

  [Test]
  public async Task RunAsync_WithUpdatedEvent_ModifiesRowAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "Original",
      Value = 10
    });
    await AppendEventAsync(eventStore, streamId, new ActionTestUpdatedEvent {
      StreamId = streamId,
      NewValue = 99
    });

    await using var storeContext = CreateDbContext();
    var perspectiveStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, "action_test");
    var runner = await CreateRunnerAsync(eventStore, perspectiveStore);

    // Act
    await runner.RunAsync(streamId, "action_test", null, CancellationToken.None);

    // Assert
    await using var verifyContext = CreateDbContext();
    var row = await verifyContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Original");
    await Assert.That(row.Data.Value).IsEqualTo(99);
  }

  [Test]
  public async Task RunAsync_WithSoftDeletedEvent_KeepsRowWithDeletedAtAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var deletedAt = DateTimeOffset.UtcNow;
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "To Soft Delete",
      Value = 5
    });
    await AppendEventAsync(eventStore, streamId, new ActionTestSoftDeletedEvent {
      StreamId = streamId,
      DeletedAt = deletedAt
    });

    await using var storeContext = CreateDbContext();
    var perspectiveStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, "action_test");
    var runner = await CreateRunnerAsync(eventStore, perspectiveStore);

    // Act
    await runner.RunAsync(streamId, "action_test", null, CancellationToken.None);

    // Assert
    await using var verifyContext = CreateDbContext();
    var row = await verifyContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("To Soft Delete");
    await Assert.That(row.Data.DeletedAt).IsNotNull();
  }

  [Test]
  public async Task RunAsync_WithPurgedEvent_RemovesRowAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "To Purge",
      Value = 1
    });
    await AppendEventAsync(eventStore, streamId, new ActionTestPurgedEvent {
      StreamId = streamId
    });

    await using var storeContext = CreateDbContext();
    var perspectiveStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, "action_test");
    var runner = await CreateRunnerAsync(eventStore, perspectiveStore);

    // Act
    await runner.RunAsync(streamId, "action_test", null, CancellationToken.None);

    // Assert
    await using var verifyContext = CreateDbContext();
    var row = await verifyContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(row).IsNull();
  }

  [Test]
  public async Task RunAsync_WithCreateUpdatePurge_RemovesRowAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "Full Lifecycle",
      Value = 100
    });
    await AppendEventAsync(eventStore, streamId, new ActionTestUpdatedEvent {
      StreamId = streamId,
      NewValue = 200
    });
    await AppendEventAsync(eventStore, streamId, new ActionTestPurgedEvent {
      StreamId = streamId
    });

    await using var storeContext = CreateDbContext();
    var perspectiveStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, "action_test");
    var runner = await CreateRunnerAsync(eventStore, perspectiveStore);

    // Act
    await runner.RunAsync(streamId, "action_test", null, CancellationToken.None);

    // Assert
    await using var verifyContext = CreateDbContext();
    var row = await verifyContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(row).IsNull();
  }

  [Test]
  public async Task RunAsync_Purge_ThenQuery_ReturnsNullAsync() {
    // Arrange: Two separate RunAsync calls simulating checkpoint-based processing
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();

    // First batch: Create
    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "Two Phase",
      Value = 50
    });

    await using var storeContext1 = CreateDbContext();
    var perspectiveStore1 = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext1, "action_test");
    var runner1 = await CreateRunnerAsync(eventStore, perspectiveStore1);

    var result1 = await runner1.RunAsync(streamId, "action_test", null, CancellationToken.None);

    // Verify row exists after first run
    await using var midContext = CreateDbContext();
    var midRow = await midContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);
    await Assert.That(midRow).IsNotNull();
    await Assert.That(midRow!.Data.Name).IsEqualTo("Two Phase");

    // Second batch: Purge (using checkpoint from first run)
    await AppendEventAsync(eventStore, streamId, new ActionTestPurgedEvent {
      StreamId = streamId
    });

    await using var storeContext2 = CreateDbContext();
    var perspectiveStore2 = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext2, "action_test");
    var runner2 = await CreateRunnerAsync(eventStore, perspectiveStore2);

    await runner2.RunAsync(streamId, "action_test", result1.LastEventId, CancellationToken.None);

    // Assert: Row should be gone after purge
    await using var verifyContext = CreateDbContext();
    var row = await verifyContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(row).IsNull();
  }

  /// <summary>
  /// Reproduces the JDNext ActiveSessions zombie bug:
  /// When multiple page refreshes occur before the PerspectiveWorker processes events,
  /// the batch contains: UpdateEvent, PurgeEvent, UpdateEvent (from 2nd refresh).
  /// The update AFTER purge passes null to Apply(), causing NullReferenceException,
  /// which prevents PurgeAsync from ever executing — the row persists as a zombie.
  /// </summary>
  [Test]
  public async Task RunAsync_WithUpdateAfterPurge_ShouldStillPurgeRowAsync() {
    // Arrange: Create the row in first run
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();

    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "Zombie Session",
      Value = 1
    });

    await using var storeContext1 = CreateDbContext();
    var perspectiveStore1 = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext1, "action_test");
    var runner1 = await CreateRunnerAsync(eventStore, perspectiveStore1);
    var result1 = await runner1.RunAsync(streamId, "action_test", null, CancellationToken.None);

    // Verify row exists
    await using var midContext = CreateDbContext();
    var midRow = await midContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);
    await Assert.That(midRow).IsNotNull();

    // Second batch: Simulates multiple page refreshes accumulating events
    // Refresh 1: update + purge
    await AppendEventAsync(eventStore, streamId, new ActionTestUpdatedEvent {
      StreamId = streamId,
      NewValue = 10
    });
    await AppendEventAsync(eventStore, streamId, new ActionTestPurgedEvent {
      StreamId = streamId
    });
    // Refresh 2: another update arrives AFTER the purge (this is the bug trigger)
    await AppendEventAsync(eventStore, streamId, new ActionTestUpdatedEvent {
      StreamId = streamId,
      NewValue = 20
    });

    await using var storeContext2 = CreateDbContext();
    var perspectiveStore2 = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext2, "action_test");
    var runner2 = await CreateRunnerAsync(eventStore, perspectiveStore2);

    // Act: Process the batch — should purge the row despite events after the purge
    await runner2.RunAsync(streamId, "action_test", result1.LastEventId, CancellationToken.None);

    // Assert: Row should be purged
    await using var verifyContext = CreateDbContext();
    var row = await verifyContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(row).IsNull();
  }

  /// <summary>
  /// Regression for the phantom-row bug: when a perspective returns <see cref="ApplyResult{TModel}.None"/>
  /// as the first (and only) event on a brand-new stream, the runner must NOT insert
  /// the pre-created default model. ApplyResult.None() means "skip write".
  /// </summary>
  [Test]
  public async Task RunAsync_WithIgnoredEventOnNewStream_InsertsNoRowAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ActionTestIgnoredEvent {
      StreamId = streamId
    });

    await using var storeContext = CreateDbContext();
    var perspectiveStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, "action_test");
    var runner = await CreateRunnerAsync(eventStore, perspectiveStore);

    // Act
    await runner.RunAsync(streamId, "action_test", null, CancellationToken.None);

    // Assert: no phantom default row
    await using var verifyContext = CreateDbContext();
    var row = await verifyContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(row).IsNull();
  }

  /// <summary>
  /// Guards against the naive fix where <c>updatedModel = null</c> unconditionally on None:
  /// Event 1 returns an updated model, Event 2 returns <see cref="ApplyResult{TModel}.None"/>.
  /// The batch must still persist Event 1's update. None means "I don't touch this event",
  /// not "discard everything accumulated so far in the batch".
  /// </summary>
  [Test]
  public async Task RunAsync_WithUpdateThenIgnoredOnNewStream_PersistsFirstUpdateAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "Created",
      Value = 7
    });
    await AppendEventAsync(eventStore, streamId, new ActionTestIgnoredEvent {
      StreamId = streamId
    });

    await using var storeContext = CreateDbContext();
    var perspectiveStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, "action_test");
    var runner = await CreateRunnerAsync(eventStore, perspectiveStore);

    // Act
    await runner.RunAsync(streamId, "action_test", null, CancellationToken.None);

    // Assert: Create is persisted; Ignored is a no-op that doesn't undo it
    await using var verifyContext = CreateDbContext();
    var row = await verifyContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Created");
    await Assert.That(row.Data.Value).IsEqualTo(7);
  }

  /// <summary>
  /// <see cref="ApplyResult{TModel}.None"/> on an existing row must leave it unchanged —
  /// no upsert overwriting to the default, no deletion.
  /// </summary>
  [Test]
  public async Task RunAsync_WithIgnoredEventOnExistingStream_LeavesRowUnchangedAsync() {
    // Arrange: two-phase to simulate a row that already exists in DB
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "Pre-existing",
      Value = 123
    });

    await using var storeContext1 = CreateDbContext();
    var perspectiveStore1 = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext1, "action_test");
    var runner1 = await CreateRunnerAsync(eventStore, perspectiveStore1);
    var result1 = await runner1.RunAsync(streamId, "action_test", null, CancellationToken.None);

    // Second batch: a single Ignored event against the existing row
    await AppendEventAsync(eventStore, streamId, new ActionTestIgnoredEvent {
      StreamId = streamId
    });

    await using var storeContext2 = CreateDbContext();
    var perspectiveStore2 = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext2, "action_test");
    var runner2 = await CreateRunnerAsync(eventStore, perspectiveStore2);

    // Act
    await runner2.RunAsync(streamId, "action_test", result1.LastEventId, CancellationToken.None);

    // Assert: row unchanged
    await using var verifyContext = CreateDbContext();
    var row = await verifyContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Data.Name).IsEqualTo("Pre-existing");
    await Assert.That(row.Data.Value).IsEqualTo(123);
  }

  /// <summary>
  /// Similar to the zombie bug but with a create event after purge.
  /// If the same stream gets a new CreatedEvent after Purge (e.g., session re-created),
  /// the runner should handle it gracefully — purge takes precedence since it was set.
  /// </summary>
  [Test]
  public async Task RunAsync_WithCreateAfterPurge_ShouldStillPurgeRowAsync() {
    // Arrange
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();

    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "Original",
      Value = 1
    });

    await using var storeContext1 = CreateDbContext();
    var perspectiveStore1 = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext1, "action_test");
    var runner1 = await CreateRunnerAsync(eventStore, perspectiveStore1);
    var result1 = await runner1.RunAsync(streamId, "action_test", null, CancellationToken.None);

    // Second batch: purge then create
    await AppendEventAsync(eventStore, streamId, new ActionTestPurgedEvent {
      StreamId = streamId
    });
    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "Recreated",
      Value = 99
    });

    await using var storeContext2 = CreateDbContext();
    var perspectiveStore2 = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext2, "action_test");
    var runner2 = await CreateRunnerAsync(eventStore, perspectiveStore2);

    // Act
    await runner2.RunAsync(streamId, "action_test", result1.LastEventId, CancellationToken.None);

    // Assert: Purge takes precedence (pendingPurge flag stays true)
    await using var verifyContext = CreateDbContext();
    var row = await verifyContext.Set<PerspectiveRow<ActionTestModel>>()
        .FirstOrDefaultAsync(r => r.Id == streamId);

    await Assert.That(row).IsNull();
  }
}
