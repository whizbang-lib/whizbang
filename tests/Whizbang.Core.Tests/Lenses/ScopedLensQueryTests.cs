using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for IScopedLensQuery<T> auto-scoping behavior.
/// Verifies that each operation creates and disposes its own service scope.
/// </summary>
[Category("Core")]
[Category("Lenses")]
public class ScopedLensQueryTests {
  // Test model
  private sealed record TestModel {
    public required Guid Id { get; init; }
    public required string Name { get; init; }
  }

  [Test]
  public async Task GetByIdAsync_CreatesScope_AndDisposesAfterQueryAsync() {
    // Arrange
    var testId = Guid.NewGuid();
    var expectedModel = new TestModel { Id = testId, Name = "Test" };

    var scopesCreated = 0;
    var scopesDisposed = 0;

    var services = new ServiceCollection();

    // Track scope creation/disposal
    services.AddScoped<ScopeTracker>(_ => {
      scopesCreated++;
      return new ScopeTracker(() => scopesDisposed++);
    });

    // Mock ILensQuery<TestModel> - scoped (takes ScopeTracker to trigger scope tracking)
    services.AddScoped<ILensQuery<TestModel>>(sp => {
      var tracker = sp.GetRequiredService<ScopeTracker>(); // Force tracker instantiation
      var mockQuery = new MockLensQuery<TestModel>();
      mockQuery.SetModel(expectedModel);
      return mockQuery;
    });

    // Register IScopedLensQuery<TestModel> (singleton)
    services.AddSingleton<IScopedLensQuery<TestModel>>(sp => {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new ScopedLensQuery<TestModel>(scopeFactory);
    });

    var rootProvider = services.BuildServiceProvider();
    var scopedQuery = rootProvider.GetRequiredService<IScopedLensQuery<TestModel>>();

    // Act
    var result = await scopedQuery.GetByIdAsync(testId);

    // Assert - Result correct
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Id).IsEqualTo(testId);
    await Assert.That(result.Name).IsEqualTo("Test");

    // Assert - Scope created and disposed
    await Assert.That(scopesCreated).IsEqualTo(1);
    await Assert.That(scopesDisposed).IsEqualTo(1);
  }

  [Test]
  public async Task ExecuteAsync_CreatesScope_AndDisposesAfterQueryAsync() {
    // Arrange
    var expectedItems = new List<TestModel>
    {
            new() { Id = Guid.NewGuid(), Name = "Item1" },
            new() { Id = Guid.NewGuid(), Name = "Item2" }
        };

    var scopesCreated = 0;
    var scopesDisposed = 0;

    var services = new ServiceCollection();

    services.AddScoped<ScopeTracker>(_ => {
      scopesCreated++;
      return new ScopeTracker(() => scopesDisposed++);
    });

    services.AddScoped<ILensQuery<TestModel>>(sp => {
      var tracker = sp.GetRequiredService<ScopeTracker>(); // Force tracker instantiation
      var mockQuery = new MockLensQuery<TestModel>();
      mockQuery.SetModels(expectedItems);
      return mockQuery;
    });

    services.AddSingleton<IScopedLensQuery<TestModel>>(sp => {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new ScopedLensQuery<TestModel>(scopeFactory);
    });

    var rootProvider = services.BuildServiceProvider();
    var scopedQuery = rootProvider.GetRequiredService<IScopedLensQuery<TestModel>>();

    // Act
    var result = await scopedQuery.ExecuteAsync(async (query, ct) => {
      var rows = query.Query.ToList();
      return rows.Select(r => r.Data).ToList();
    });

    // Assert - Result correct
    await Assert.That(result).Count().IsEqualTo(2);
    await Assert.That(result[0].Name).IsEqualTo("Item1");
    await Assert.That(result[1].Name).IsEqualTo("Item2");

    // Assert - Scope created and disposed
    await Assert.That(scopesCreated).IsEqualTo(1);
    await Assert.That(scopesDisposed).IsEqualTo(1);
  }

  [Test]
  public async Task QueryAsync_CreatesScope_AndStreamResultsAsync() {
    // Arrange
    var expectedItems = new List<TestModel>
    {
            new() { Id = Guid.NewGuid(), Name = "Item1" },
            new() { Id = Guid.NewGuid(), Name = "Item2" },
            new() { Id = Guid.NewGuid(), Name = "Item3" }
        };

    var scopesCreated = 0;
    var scopesDisposed = 0;

    var services = new ServiceCollection();

    services.AddScoped<ScopeTracker>(_ => {
      scopesCreated++;
      return new ScopeTracker(() => scopesDisposed++);
    });

    services.AddScoped<ILensQuery<TestModel>>(sp => {
      var tracker = sp.GetRequiredService<ScopeTracker>(); // Force tracker instantiation
      var mockQuery = new MockLensQuery<TestModel>();
      mockQuery.SetModels(expectedItems);
      return mockQuery;
    });

    services.AddSingleton<IScopedLensQuery<TestModel>>(sp => {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new ScopedLensQuery<TestModel>(scopeFactory);
    });

    var rootProvider = services.BuildServiceProvider();
    var scopedQuery = rootProvider.GetRequiredService<IScopedLensQuery<TestModel>>();

    // Act
    var results = new List<PerspectiveRow<TestModel>>();
    await foreach (var row in scopedQuery.QueryAsync(query => query.Query)) {
      results.Add(row);
    }

    // Assert - Results correct
    await Assert.That(results).Count().IsEqualTo(3);
    await Assert.That(results[0].Data.Name).IsEqualTo("Item1");
    await Assert.That(results[1].Data.Name).IsEqualTo("Item2");
    await Assert.That(results[2].Data.Name).IsEqualTo("Item3");

    // Assert - Scope created and disposed after enumeration
    await Assert.That(scopesCreated).IsEqualTo(1);
    await Assert.That(scopesDisposed).IsEqualTo(1);
  }

  [Test]
  public async Task ConcurrentQueries_CreateSeparateScopesAsync() {
    // Arrange
    var model1 = new TestModel { Id = Guid.NewGuid(), Name = "Model1" };
    var model2 = new TestModel { Id = Guid.NewGuid(), Name = "Model2" };

    var scopesCreated = 0;
    var scopesDisposed = 0;
    var maxConcurrentScopes = 0;

    var services = new ServiceCollection();

    services.AddScoped<ScopeTracker>(_ => {
      var currentScopes = Interlocked.Increment(ref scopesCreated);
      var currentActive = currentScopes - scopesDisposed;
      if (currentActive > maxConcurrentScopes) {
        maxConcurrentScopes = currentActive;
      }

      return new ScopeTracker(() => Interlocked.Increment(ref scopesDisposed));
    });

    services.AddScoped<ILensQuery<TestModel>>(sp => {
      var tracker = sp.GetRequiredService<ScopeTracker>(); // Force tracker instantiation
      var mockQuery = new MockLensQuery<TestModel>();
      mockQuery.SetModel(model1);
      return mockQuery;
    });

    services.AddSingleton<IScopedLensQuery<TestModel>>(sp => {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new ScopedLensQuery<TestModel>(scopeFactory);
    });

    var rootProvider = services.BuildServiceProvider();
    var scopedQuery = rootProvider.GetRequiredService<IScopedLensQuery<TestModel>>();

    // Act - Execute multiple queries concurrently
    var tasks = new[]
    {
            scopedQuery.GetByIdAsync(model1.Id),
            scopedQuery.GetByIdAsync(model2.Id),
            scopedQuery.GetByIdAsync(model1.Id)
        };

    await Task.WhenAll(tasks);

    // Assert - Each query created its own scope
    await Assert.That(scopesCreated).IsEqualTo(3);
    await Assert.That(scopesDisposed).IsEqualTo(3);

    // Assert - At least one scope was active (proves scopes were created)
    await Assert.That(maxConcurrentScopes).IsGreaterThanOrEqualTo(1);
  }

  // Helper classes for testing

  /// <summary>
  /// Tracks scope disposal for testing
  /// </summary>
  private sealed class ScopeTracker : IDisposable {
    private readonly Action _onDispose;

    public ScopeTracker(Action onDispose) {
      _onDispose = onDispose;
    }

    public void Dispose() {
      _onDispose();
    }
  }

  /// <summary>
  /// Mock ILensQuery implementation for testing
  /// </summary>
  private sealed class MockLensQuery<TModel> : ILensQuery<TModel> where TModel : class {
    private readonly List<TModel> _models = new();

    public IQueryable<PerspectiveRow<TModel>> Query =>
        _models.Select(m => new PerspectiveRow<TModel> {
          Id = Guid.NewGuid(),
          Data = m,
          Metadata = new PerspectiveMetadata {
            EventType = "TestEvent",
            EventId = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow
          },
          Scope = new PerspectiveScope {
            TenantId = "test-tenant"
          },
          CreatedAt = DateTime.UtcNow,
          UpdatedAt = DateTime.UtcNow,
          Version = 1
        }).AsQueryable();

    public void SetModel(TModel model) {
      _models.Clear();
      _models.Add(model);
    }

    public void SetModels(IEnumerable<TModel> models) {
      _models.Clear();
      _models.AddRange(models);
    }

    public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      return _models.FirstOrDefault();
    }
  }
}
