using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Tests for ILensQueryFactory<T> manual scope control behavior.
/// Verifies that factory creates disposable scoped instances for batch operations.
/// </summary>
[Category("Core")]
[Category("Lenses")]
public class LensQueryFactoryTests {
  // Test model
  private sealed record TestModel {
    public required Guid Id { get; init; }
    public required string Name { get; init; }
  }

  [Test]
  public async Task CreateScoped_ReturnsDisposableScopedQueryAsync() {
    // Arrange
    var testId = Guid.NewGuid();
    var expectedModel = new TestModel { Id = testId, Name = "Test" };

    var services = new ServiceCollection();

    services.AddScoped<ILensQuery<TestModel>>(sp => {
      var mockQuery = new MockLensQuery<TestModel>();
      mockQuery.SetModel(expectedModel);
      return mockQuery;
    });

    services.AddSingleton<ILensQueryFactory<TestModel>>(sp => {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new LensQueryFactory<TestModel>(scopeFactory);
    });

    var rootProvider = services.BuildServiceProvider();
    var factory = rootProvider.GetRequiredService<ILensQueryFactory<TestModel>>();

    // Act
    using var scopedQuery = factory.CreateScoped();

    // Assert - Returns disposable wrapper
    await Assert.That(scopedQuery).IsNotNull();
    await Assert.That(scopedQuery.Value).IsNotNull();
    await Assert.That(scopedQuery.Value).IsTypeOf<MockLensQuery<TestModel>>();

    // Assert - Can query through the wrapper
    var result = await scopedQuery.Value.GetByIdAsync(testId);
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Id).IsEqualTo(testId);
  }

  [Test]
  public async Task ScopedQuery_DisposesScope_WhenDisposedAsync() {
    // Arrange
    var scopesCreated = 0;
    var scopesDisposed = 0;

    var services = new ServiceCollection();

    services.AddScoped<ScopeTracker>(_ => {
      scopesCreated++;
      return new ScopeTracker(() => scopesDisposed++);
    });

    services.AddScoped<ILensQuery<TestModel>>(sp => {
      var tracker = sp.GetRequiredService<ScopeTracker>(); // Force tracker instantiation
      return new MockLensQuery<TestModel>();
    });

    services.AddSingleton<ILensQueryFactory<TestModel>>(sp => {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new LensQueryFactory<TestModel>(scopeFactory);
    });

    var rootProvider = services.BuildServiceProvider();
    var factory = rootProvider.GetRequiredService<ILensQueryFactory<TestModel>>();

    // Act
    var scopedQuery = factory.CreateScoped();
    await Assert.That(scopesCreated).IsEqualTo(1);
    await Assert.That(scopesDisposed).IsEqualTo(0);

    scopedQuery.Dispose();

    // Assert - Scope disposed after Dispose() called
    await Assert.That(scopesDisposed).IsEqualTo(1);
  }

  [Test]
  public async Task ScopedQuery_SharesSameDbContext_WithinScopeAsync() {
    // Arrange
    var scopesCreated = 0;

    var services = new ServiceCollection();

    services.AddScoped<ScopeTracker>(_ => {
      scopesCreated++;
      return new ScopeTracker(() => { });
    });

    services.AddScoped<ILensQuery<TestModel>>(sp => {
      var tracker = sp.GetRequiredService<ScopeTracker>(); // Force tracker instantiation
      return new MockLensQuery<TestModel>();
    });

    services.AddSingleton<ILensQueryFactory<TestModel>>(sp => {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new LensQueryFactory<TestModel>(scopeFactory);
    });

    var rootProvider = services.BuildServiceProvider();
    var factory = rootProvider.GetRequiredService<ILensQueryFactory<TestModel>>();

    // Act - Multiple queries within same scope
    using var scopedQuery = factory.CreateScoped();

    var query1 = scopedQuery.Value.Query.ToList();
    var query2 = scopedQuery.Value.Query.ToList();
    var query3 = scopedQuery.Value.Query.ToList();

    // Assert - Only one scope created (all queries share it)
    await Assert.That(scopesCreated).IsEqualTo(1);
  }

  [Test]
  public async Task MultipleScopedQueries_CreateSeparateScopesAsync() {
    // Arrange
    var scopesCreated = 0;
    var scopesDisposed = 0;

    var services = new ServiceCollection();

    services.AddScoped<ScopeTracker>(_ => {
      scopesCreated++;
      return new ScopeTracker(() => scopesDisposed++);
    });

    services.AddScoped<ILensQuery<TestModel>>(sp => {
      var tracker = sp.GetRequiredService<ScopeTracker>();
      return new MockLensQuery<TestModel>();
    });

    services.AddSingleton<ILensQueryFactory<TestModel>>(sp => {
      var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
      return new LensQueryFactory<TestModel>(scopeFactory);
    });

    var rootProvider = services.BuildServiceProvider();
    var factory = rootProvider.GetRequiredService<ILensQueryFactory<TestModel>>();

    // Act - Create multiple scoped queries
    using var scope1 = factory.CreateScoped();
    using var scope2 = factory.CreateScoped();
    using var scope3 = factory.CreateScoped();

    // Assert - Each CreateScoped() creates a new scope
    await Assert.That(scopesCreated).IsEqualTo(3);

    // Dispose all
    scope1.Dispose();
    scope2.Dispose();
    scope3.Dispose();

    await Assert.That(scopesDisposed).IsEqualTo(3);
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

    public async Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      return _models.FirstOrDefault();
    }
  }
}
