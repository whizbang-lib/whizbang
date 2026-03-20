using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;

namespace Whizbang.Core.Tests.Lenses;

/// <summary>
/// Unit tests for FactoryOwnedLensQuery<T> wrapper class.
/// Verifies delegation to inner query and proper factory disposal.
/// </summary>
/// <docs>lenses/lens-query-factory</docs>
[Category("Core")]
[Category("Lenses")]
public class FactoryOwnedLensQueryTests {
  // Test model
  private sealed record TestModel {
    public required Guid Id { get; init; }
    public required string Name { get; init; }
  }

  #region Constructor Tests

  [Test]
  public async Task Constructor_WithValidFactory_CreatesInstanceAsync() {
    // Arrange
    var factory = new MockLensQueryFactory();
    factory.SetQuery(new MockLensQuery<TestModel>());

    // Act
    var wrapper = new FactoryOwnedLensQuery<TestModel>(factory);

    // Assert
    await Assert.That(wrapper).IsNotNull();
  }

  [Test]
  public async Task Constructor_WithNullFactory_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    await Assert.ThrowsAsync<ArgumentNullException>(() => {
      _ = new FactoryOwnedLensQuery<TestModel>(null!);
      return Task.CompletedTask;
    });
  }

  [Test]
  public async Task Constructor_CallsGetQueryOnFactory_Async() {
    // Arrange
    var factory = new MockLensQueryFactory();
    var mockQuery = new MockLensQuery<TestModel>();
    factory.SetQuery(mockQuery);

    // Act
    _ = new FactoryOwnedLensQuery<TestModel>(factory);

    // Assert
    await Assert.That(factory.GetQueryCallCount).IsEqualTo(1);
  }

  #endregion

  #region Query Property Tests

  [Test]
  public async Task Query_ReturnsInnerQueryProperty_Async() {
    // Arrange
    var testModel = new TestModel { Id = Guid.NewGuid(), Name = "Test" };
    var factory = new MockLensQueryFactory();
    var mockQuery = new MockLensQuery<TestModel>();
    mockQuery.SetModel(testModel);
    factory.SetQuery(mockQuery);

    var wrapper = new FactoryOwnedLensQuery<TestModel>(factory);

    // Act
    var query = wrapper.Query;

    // Assert
    await Assert.That(query).IsNotNull();
    var result = query.FirstOrDefault();
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Data.Name).IsEqualTo("Test");
  }

  [Test]
  public async Task Query_WhenCalledMultipleTimes_ReturnsSameQueryable_Async() {
    // Arrange
    var factory = new MockLensQueryFactory();
    factory.SetQuery(new MockLensQuery<TestModel>());
    var wrapper = new FactoryOwnedLensQuery<TestModel>(factory);

    // Act
    var query1 = wrapper.Query;
    var query2 = wrapper.Query;

    // Assert - Multiple calls should return same queryable from inner query
    await Assert.That(query1).IsNotNull();
    await Assert.That(query2).IsNotNull();
  }

  #endregion

  #region GetByIdAsync Tests

  [Test]
  public async Task GetByIdAsync_DelegatesToInnerQuery_Async() {
    // Arrange
    var testId = Guid.NewGuid();
    var testModel = new TestModel { Id = testId, Name = "Test Model" };
    var factory = new MockLensQueryFactory();
    var mockQuery = new MockLensQuery<TestModel>();
    mockQuery.SetModel(testModel);
    factory.SetQuery(mockQuery);

    var wrapper = new FactoryOwnedLensQuery<TestModel>(factory);

    // Act
    var result = await wrapper.GetByIdAsync(testId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Name).IsEqualTo("Test Model");
    await Assert.That(mockQuery.GetByIdCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task GetByIdAsync_PassesCancellationToken_Async() {
    // Arrange
    var factory = new MockLensQueryFactory();
    var mockQuery = new MockLensQuery<TestModel>();
    factory.SetQuery(mockQuery);
    var wrapper = new FactoryOwnedLensQuery<TestModel>(factory);
    using var cts = new CancellationTokenSource();

    // Act
    await wrapper.GetByIdAsync(Guid.NewGuid(), cts.Token);

    // Assert
    await Assert.That(mockQuery.LastCancellationToken).IsEqualTo(cts.Token);
  }

  [Test]
  public async Task GetByIdAsync_WhenInnerReturnsNull_ReturnsNull_Async() {
    // Arrange
    var factory = new MockLensQueryFactory();
    var mockQuery = new MockLensQuery<TestModel>();
    // Don't set a model, so GetByIdAsync returns null
    factory.SetQuery(mockQuery);
    var wrapper = new FactoryOwnedLensQuery<TestModel>(factory);

    // Act
    var result = await wrapper.GetByIdAsync(Guid.NewGuid());

    // Assert
    await Assert.That(result).IsNull();
  }

  [Test]
  public async Task GetByIdAsync_WhenInnerReturnsValue_ReturnsValue_Async() {
    // Arrange
    var testId = Guid.NewGuid();
    var testModel = new TestModel { Id = testId, Name = "Found Model" };
    var factory = new MockLensQueryFactory();
    var mockQuery = new MockLensQuery<TestModel>();
    mockQuery.SetModel(testModel);
    factory.SetQuery(mockQuery);

    var wrapper = new FactoryOwnedLensQuery<TestModel>(factory);

    // Act
    var result = await wrapper.GetByIdAsync(testId);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result!.Id).IsEqualTo(testId);
    await Assert.That(result.Name).IsEqualTo("Found Model");
  }

  #endregion

  #region DisposeAsync Tests

  [Test]
  public async Task DisposeAsync_DisposesFactory_Async() {
    // Arrange
    var factory = new MockLensQueryFactory();
    factory.SetQuery(new MockLensQuery<TestModel>());
    var wrapper = new FactoryOwnedLensQuery<TestModel>(factory);

    // Act
    await wrapper.DisposeAsync();

    // Assert
    await Assert.That(factory.DisposeCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task DisposeAsync_WhenCalledTwice_OnlyDisposesOnce_Async() {
    // Arrange
    var factory = new MockLensQueryFactory();
    factory.SetQuery(new MockLensQuery<TestModel>());
    var wrapper = new FactoryOwnedLensQuery<TestModel>(factory);

    // Act
    await wrapper.DisposeAsync();
    await wrapper.DisposeAsync();

    // Assert
    await Assert.That(factory.DisposeCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task DisposeAsync_WhenNotDisposed_DisposesFactory_Async() {
    // Arrange
    var factory = new MockLensQueryFactory();
    factory.SetQuery(new MockLensQuery<TestModel>());
    var wrapper = new FactoryOwnedLensQuery<TestModel>(factory);

    await Assert.That(factory.DisposeCallCount).IsEqualTo(0);

    // Act
    await wrapper.DisposeAsync();

    // Assert
    await Assert.That(factory.DisposeCallCount).IsEqualTo(1);
  }

  [Test]
  public async Task DisposeAsync_WhenAlreadyDisposed_DoesNotThrow_Async() {
    // Arrange
    var factory = new MockLensQueryFactory();
    factory.SetQuery(new MockLensQuery<TestModel>());
    var wrapper = new FactoryOwnedLensQuery<TestModel>(factory);

    await wrapper.DisposeAsync();

    // Act & Assert - Second dispose should not throw
    await wrapper.DisposeAsync();
    await Assert.That(factory.DisposeCallCount).IsEqualTo(1);
  }

  #endregion

  #region Helper Classes

  /// <summary>
  /// Mock ILensQueryFactory for testing.
  /// </summary>
  private sealed class MockLensQueryFactory : ILensQueryFactory {
    private readonly Dictionary<Type, object> _queries = [];
    public int GetQueryCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }

    public void SetQuery<TModel>(ILensQuery<TModel> query) where TModel : class {
      _queries[typeof(TModel)] = query;
    }

    public ILensQuery<TModel> GetQuery<TModel>() where TModel : class {
      GetQueryCallCount++;
      if (_queries.TryGetValue(typeof(TModel), out var query)) {
        return (ILensQuery<TModel>)query;
      }
      throw new InvalidOperationException($"No query registered for type {typeof(TModel).Name}");
    }

    public void Dispose() {
      DisposeCallCount++;
    }

    public ValueTask DisposeAsync() {
      DisposeCallCount++;
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Mock ILensQuery implementation for testing.
  /// </summary>
  private sealed class MockLensQuery<TModel> : ILensQuery<TModel> where TModel : class {
    private readonly List<TModel> _models = [];
    public int GetByIdCallCount { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }

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

    public Task<TModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
      GetByIdCallCount++;
      LastCancellationToken = cancellationToken;
      return Task.FromResult(_models.FirstOrDefault());
    }
  }

  #endregion
}
