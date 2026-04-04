using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Tests for PerspectiveRebuilder — verifies all rebuild modes and error handling.
/// </summary>
public class PerspectiveRebuilderTests {
  [Test]
  public async Task RebuildInPlaceAsync_WithRegisteredPerspective_ProcessesAllStreamsAsync() {
    // Arrange
    var runner = new FakePerspectiveRunner();
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var eventStoreQuery = new FakeEventStoreQuery([Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("TestPerspective");

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(3);
    await Assert.That(result.PerspectiveName).IsEqualTo("TestPerspective");
    await Assert.That(runner.RunCount).IsEqualTo(3);
  }

  [Test]
  public async Task RebuildStreamsAsync_WithSpecificStreams_OnlyProcessesThoseAsync() {
    // Arrange
    var runner = new FakePerspectiveRunner();
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var eventStoreQuery = new FakeEventStoreQuery([]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);
    var targetStreams = new[] { Guid.NewGuid(), Guid.NewGuid() };

    // Act
    var result = await rebuilder.RebuildStreamsAsync("TestPerspective", targetStreams);

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);
    await Assert.That(runner.RunCount).IsEqualTo(2);
  }

  [Test]
  public async Task RebuildInPlaceAsync_WithUnknownPerspective_ReturnsFailureAsync() {
    // Arrange
    var registry = new FakePerspectiveRunnerRegistry(runner: null);
    var eventStoreQuery = new FakeEventStoreQuery([]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("NonexistentPerspective");

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).Contains("No runner found");
  }

  [Test]
  public async Task GetRebuildStatusAsync_WithNoActiveRebuild_ReturnsNullAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(new FakePerspectiveRunnerRegistry(null));
    services.AddSingleton<IEventStoreQuery>(new FakeEventStoreQuery([]));
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var status = await rebuilder.GetRebuildStatusAsync("TestPerspective");

    // Assert
    await Assert.That(status).IsNull();
  }

  [Test]
  public async Task RebuildInPlaceAsync_WithFailingStream_ContinuesWithOtherStreamsAsync() {
    // Arrange
    var runner = new FakePerspectiveRunner { FailOnStreamIndex = 1 };
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var streams = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
    var eventStoreQuery = new FakeEventStoreQuery(streams);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("TestPerspective");

    // Assert — should still succeed overall, but only 2 streams processed
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);
    await Assert.That(runner.RunCount).IsEqualTo(3); // All 3 attempted
  }

  [Test]
  public async Task RebuildBlueGreenAsync_CompletesSuccessfullyAsync() {
    // Arrange
    var runner = new FakePerspectiveRunner();
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var eventStoreQuery = new FakeEventStoreQuery([Guid.NewGuid()]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildBlueGreenAsync("TestPerspective");

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.Duration.TotalMilliseconds).IsGreaterThanOrEqualTo(0);
  }

  [Test]
  public async Task RebuildInPlaceAsync_WithUnknownPerspective_ErrorIncludesRegisteredNamesAsync() {
    // Arrange — covers line 60: detailed error message with registered perspectives
    var registry = new FakePerspectiveRunnerRegistryWithInfo(runner: null, [
      new PerspectiveRegistrationInfo("MyApp.OrderPerspective", "global::MyApp.OrderPerspective", "global::MyApp.OrderModel", []),
      new PerspectiveRegistrationInfo("MyApp.InventoryPerspective", "global::MyApp.InventoryPerspective", "global::MyApp.InventoryModel", [])
    ]);
    var eventStoreQuery = new FakeEventStoreQuery([]);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("NonexistentPerspective");

    // Assert — error should include the registered perspective names
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).Contains("No runner found");
    await Assert.That(result.Error).Contains("MyApp.OrderPerspective");
    await Assert.That(result.Error).Contains("MyApp.InventoryPerspective");
  }

  [Test]
  public async Task RebuildInPlaceAsync_WhenScopeCreationThrows_ReturnsFailureAsync() {
    // Arrange — covers lines 103-106 (outer catch block)
    var services = new ServiceCollection();
    // Don't register IPerspectiveRunnerRegistry — GetRequiredService will throw
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act
    var result = await rebuilder.RebuildInPlaceAsync("TestPerspective");

    // Assert
    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).IsNotNull();
    await Assert.That(result.PerspectiveName).IsEqualTo("TestPerspective");
  }

  [Test]
  public async Task RebuildInPlaceAsync_WithSyncQueryable_UsesNonAsyncFallbackAsync() {
    // Arrange — covers lines 136-138 (sync IQueryable fallback in ToListAsync)
    // The default FakeEventStoreQuery returns a regular IQueryable (not IAsyncEnumerable),
    // so it exercises the else branch in ToListAsync
    var runner = new FakePerspectiveRunner();
    var registry = new FakePerspectiveRunnerRegistry(runner);
    var streamIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
    var eventStoreQuery = new FakeEventStoreQuery(streamIds);

    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(registry);
    services.AddSingleton<IEventStoreQuery>(eventStoreQuery);
    var sp = services.BuildServiceProvider();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

    var rebuilder = new PerspectiveRebuilder(scopeFactory, NullLogger<PerspectiveRebuilder>.Instance);

    // Act — FakeEventStoreQuery.Query returns a plain IQueryable (not IAsyncEnumerable),
    // triggering the sync fallback path in QueryableExtensions.ToListAsync
    var result = await rebuilder.RebuildInPlaceAsync("TestPerspective");

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);
  }

  // --- Test Doubles ---

  private sealed class FakePerspectiveRunnerRegistryWithInfo(
      IPerspectiveRunner? runner,
      IReadOnlyList<PerspectiveRegistrationInfo> registrations) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => registrations;
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class FakePerspectiveRunner : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object); // Fake — no real perspective type
    public int RunCount { get; private set; }
    public int FailOnStreamIndex { get; init; } = -1;

    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken) {
      var index = RunCount;
      RunCount++;

      if (index == FailOnStreamIndex) {
        throw new InvalidOperationException($"Simulated failure on stream index {index}");
      }

      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = Guid.NewGuid(),
        Status = PerspectiveProcessingStatus.Completed
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
        RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
  }

  private sealed class FakePerspectiveRunnerRegistry(IPerspectiveRunner? runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  private sealed class FakeEventStoreQuery(Guid[] streamIds) : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query =>
        streamIds.Select(id => new EventStoreRecord {
          Id = Guid.NewGuid(),
          StreamId = id,
          AggregateId = id,
          AggregateType = "Test",
          Version = 1,
          EventType = "TestEvent",
          EventData = JsonDocument.Parse("{}").RootElement,
          Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
          CreatedAt = DateTime.UtcNow
        }).AsQueryable();

    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) =>
        Query.Where(e => e.StreamId == streamId);

    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) =>
        Query.Where(e => e.EventType == eventType);
  }
}
