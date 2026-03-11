using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core;
using Whizbang.Core.Diagnostics;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Perspectives.Sync;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for <see cref="ISyncAwareLensQuery{TModel}"/> and sync-aware lens query extensions.
/// </summary>
/// <docs>core-concepts/perspectives/perspective-sync</docs>
public class SyncAwareLensQueryTests {
  // Test model
  private sealed class TestModel {
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
  }

  // Test perspective type
  private sealed class TestPerspective { }

  // ==========================================================================
  // ISyncAwareLensQuery interface tests
  // ==========================================================================

  [Test]
  public async Task ISyncAwareLensQuery_HasQueryPropertyAsync() {
    // Verify the interface has the expected property
    var queryProperty = typeof(ISyncAwareLensQuery<TestModel>).GetProperty("Query");

    await Assert.That(queryProperty).IsNotNull();
    await Assert.That(queryProperty!.PropertyType).IsEqualTo(typeof(IQueryable<PerspectiveRow<TestModel>>));
  }

  [Test]
  public async Task ISyncAwareLensQuery_HasGetByIdAsyncMethodAsync() {
    var method = typeof(ISyncAwareLensQuery<TestModel>).GetMethod("GetByIdAsync");

    await Assert.That(method).IsNotNull();
  }

  // ==========================================================================
  // SyncAwareLensQuery wrapper tests
  // ==========================================================================

  [Test]
  public async Task SyncAwareLensQuery_Constructor_StoresDependenciesAsync() {
    var mockQuery = new MockLensQuery<TestModel>();
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);
    var options = SyncFilter.All().Build();

    var syncQuery = new SyncAwareLensQuery<TestModel>(mockQuery, awaiter, typeof(TestPerspective), options);

    await Assert.That(syncQuery).IsNotNull();
  }

  [Test]
  public async Task SyncAwareLensQuery_Query_ReturnsDelegatedQueryAsync() {
    var mockQuery = new MockLensQuery<TestModel>();
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);
    var options = SyncFilter.All().Build();

    var syncQuery = new SyncAwareLensQuery<TestModel>(mockQuery, awaiter, typeof(TestPerspective), options);

    await Assert.That(syncQuery.Query).IsNotNull();
  }

  [Test]
  public async Task SyncAwareLensQuery_GetByIdAsync_WaitsForSyncBeforeQueryingAsync() {
    var mockQuery = new MockLensQuery<TestModel>();
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);
    var options = SyncFilter.All().WithTimeout(TimeSpan.FromMilliseconds(100)).Build();

    var syncQuery = new SyncAwareLensQuery<TestModel>(mockQuery, awaiter, typeof(TestPerspective), options);

    // With no pending events, should return immediately
    var result = await syncQuery.GetByIdAsync(Guid.NewGuid());

    await Assert.That(mockQuery.GetByIdAsyncCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task SyncAwareLensQuery_GetByIdAsync_ReturnsModelFromUnderlyingQueryAsync() {
    var testId = Guid.NewGuid();
    var expectedModel = new TestModel { Id = testId, Name = "Test" };
    var mockQuery = new MockLensQuery<TestModel> { ModelToReturn = expectedModel };
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);
    var options = SyncFilter.All().Build();

    var syncQuery = new SyncAwareLensQuery<TestModel>(mockQuery, awaiter, typeof(TestPerspective), options);

    var result = await syncQuery.GetByIdAsync(testId);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("Test");
  }

  // ==========================================================================
  // ILensQuery extension method tests - Generic overloads
  // ==========================================================================

  [Test]
  public async Task LensQueryExtensions_WithSync_Generic_ReturnsWrappedQueryAsync() {
    var mockQuery = new MockLensQuery<TestModel>();
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);
    var options = SyncFilter.All().Build();

    var syncQuery = mockQuery.WithSync<TestModel, TestPerspective>(awaiter, options);

    await Assert.That(syncQuery).IsNotNull();
    await Assert.That(syncQuery).IsTypeOf<SyncAwareLensQuery<TestModel>>();
  }

  [Test]
  public async Task LensQueryExtensions_GetByIdAsync_Generic_WaitsAndQueriesAsync() {
    var testId = Guid.NewGuid();
    var expectedModel = new TestModel { Id = testId, Name = "Test" };
    var mockQuery = new MockLensQuery<TestModel> { ModelToReturn = expectedModel };
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);
    var options = SyncFilter.All().Build();

    var result = await mockQuery.GetByIdAsync<TestModel, TestPerspective>(testId, awaiter, options);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("Test");
  }

  // ==========================================================================
  // ILensQuery extension method tests - Type parameter overloads
  // ==========================================================================

  [Test]
  public async Task LensQueryExtensions_WithSync_TypeParam_ReturnsWrappedQueryAsync() {
    var mockQuery = new MockLensQuery<TestModel>();
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);
    var options = SyncFilter.All().Build();

    var syncQuery = mockQuery.WithSync(awaiter, typeof(TestPerspective), options);

    await Assert.That(syncQuery).IsNotNull();
    await Assert.That(syncQuery).IsTypeOf<SyncAwareLensQuery<TestModel>>();
  }

  [Test]
  public async Task LensQueryExtensions_GetByIdAsync_TypeParam_WaitsAndQueriesAsync() {
    var testId = Guid.NewGuid();
    var expectedModel = new TestModel { Id = testId, Name = "Test" };
    var mockQuery = new MockLensQuery<TestModel> { ModelToReturn = expectedModel };
    var tracker = new ScopedEventTracker();
    var coordinator = new MockWorkCoordinator();
    var clock = new DebuggerAwareClock(new DebuggerAwareClockOptions { Mode = DebuggerDetectionMode.Disabled });
    var awaiter = new PerspectiveSyncAwaiter(coordinator, clock, NullLogger<PerspectiveSyncAwaiter>.Instance, new SyncEventTracker(), tracker);
    var options = SyncFilter.All().Build();

    var result = await mockQuery.GetByIdAsync(testId, awaiter, typeof(TestPerspective), options);

    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("Test");
  }

  // ==========================================================================
  // Mock implementation for testing
  // ==========================================================================

  private sealed class MockLensQuery<TModel> : ILensQuery<TModel> where TModel : class {
    public TModel? ModelToReturn { get; set; }
    public int GetByIdAsyncCallCount { get; private set; }

    public IQueryable<PerspectiveRow<TModel>> Query =>
        Enumerable.Empty<PerspectiveRow<TModel>>().AsQueryable();

    public Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
      GetByIdAsyncCallCount++;
      return Task.FromResult(ModelToReturn);
    }
  }
}
