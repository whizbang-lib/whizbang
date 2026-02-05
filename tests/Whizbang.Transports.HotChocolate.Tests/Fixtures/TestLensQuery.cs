using Whizbang.Core.Lenses;

namespace Whizbang.Transports.HotChocolate.Tests.Fixtures;

/// <summary>
/// Test read model for integration testing.
/// </summary>
public record OrderReadModel {
  public Guid Id { get; init; }
  public string CustomerName { get; init; } = string.Empty;
  public string Status { get; init; } = string.Empty;
  public decimal TotalAmount { get; init; }
  public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Test read model for product queries.
/// </summary>
public record ProductReadModel {
  public Guid Id { get; init; }
  public string Name { get; init; } = string.Empty;
  public string Category { get; init; } = string.Empty;
  public decimal Price { get; init; }
  public int StockCount { get; init; }
}

/// <summary>
/// Test lens interface for orders.
/// </summary>
[GraphQLLens(QueryName = "orders")]
public interface IOrderLens : ILensQuery<OrderReadModel> { }

/// <summary>
/// Test lens interface for products with custom configuration.
/// </summary>
[GraphQLLens(
    QueryName = "products",
    EnablePaging = true,
    DefaultPageSize = 10,
    MaxPageSize = 50)]
public interface IProductLens : ILensQuery<ProductReadModel> { }

/// <summary>
/// Test lens with filtering only.
/// </summary>
[GraphQLLens(
    QueryName = "filteredItems",
    EnableFiltering = true,
    EnableSorting = false,
    EnablePaging = false)]
public interface IFilterOnlyLens : ILensQuery<OrderReadModel> { }

/// <summary>
/// In-memory test lens implementation for orders.
/// </summary>
public class TestOrderLens : IOrderLens {
  private readonly List<PerspectiveRow<OrderReadModel>> _data;

  public TestOrderLens(IEnumerable<PerspectiveRow<OrderReadModel>>? data = null) {
    _data = data?.ToList() ?? [];
  }

  public IQueryable<PerspectiveRow<OrderReadModel>> Query => _data.AsQueryable();

  public Task<OrderReadModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    var row = _data.FirstOrDefault(r => r.Id == id);
    return Task.FromResult(row?.Data);
  }

  public void AddData(IEnumerable<PerspectiveRow<OrderReadModel>> rows) {
    _data.AddRange(rows);
  }
}

/// <summary>
/// In-memory test lens implementation for products.
/// </summary>
public class TestProductLens : IProductLens {
  private readonly List<PerspectiveRow<ProductReadModel>> _data;

  public TestProductLens(IEnumerable<PerspectiveRow<ProductReadModel>>? data = null) {
    _data = data?.ToList() ?? [];
  }

  public IQueryable<PerspectiveRow<ProductReadModel>> Query => _data.AsQueryable();

  public Task<ProductReadModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    var row = _data.FirstOrDefault(r => r.Id == id);
    return Task.FromResult(row?.Data);
  }

  public void AddData(IEnumerable<PerspectiveRow<ProductReadModel>> rows) {
    _data.AddRange(rows);
  }
}

/// <summary>
/// In-memory test lens for filter-only tests.
/// </summary>
public class TestFilterOnlyLens : IFilterOnlyLens {
  private readonly List<PerspectiveRow<OrderReadModel>> _data;

  public TestFilterOnlyLens(IEnumerable<PerspectiveRow<OrderReadModel>>? data = null) {
    _data = data?.ToList() ?? [];
  }

  public IQueryable<PerspectiveRow<OrderReadModel>> Query => _data.AsQueryable();

  public Task<OrderReadModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) {
    var row = _data.FirstOrDefault(r => r.Id == id);
    return Task.FromResult(row?.Data);
  }

  public void AddData(IEnumerable<PerspectiveRow<OrderReadModel>> rows) {
    _data.AddRange(rows);
  }
}

/// <summary>
/// Factory for creating test data.
/// </summary>
public static class TestDataFactory {
  public static PerspectiveRow<OrderReadModel> CreateOrderRow(
      Guid? id = null,
      string? customerName = null,
      string? status = null,
      decimal? totalAmount = null,
      PerspectiveScope? scope = null) {
    var rowId = id ?? Guid.NewGuid();
    var now = DateTime.UtcNow;
    var data = new OrderReadModel {
      Id = rowId,
      CustomerName = customerName ?? "Test Customer",
      Status = status ?? "Pending",
      TotalAmount = totalAmount ?? 100.00m,
      CreatedAt = now
    };

    return new PerspectiveRow<OrderReadModel> {
      Id = rowId,
      Data = data,
      Metadata = new PerspectiveMetadata {
        EventType = "OrderCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = now,
        CorrelationId = Guid.NewGuid().ToString(),
        CausationId = Guid.NewGuid().ToString()
      },
      Scope = scope ?? new PerspectiveScope(),
      CreatedAt = now,
      UpdatedAt = now,
      Version = 1
    };
  }

  public static PerspectiveRow<ProductReadModel> CreateProductRow(
      Guid? id = null,
      string? name = null,
      string? category = null,
      decimal? price = null,
      int? stockCount = null) {
    var rowId = id ?? Guid.NewGuid();
    var now = DateTime.UtcNow;
    var data = new ProductReadModel {
      Id = rowId,
      Name = name ?? "Test Product",
      Category = category ?? "Electronics",
      Price = price ?? 99.99m,
      StockCount = stockCount ?? 10
    };

    return new PerspectiveRow<ProductReadModel> {
      Id = rowId,
      Data = data,
      Metadata = new PerspectiveMetadata {
        EventType = "ProductCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = now,
        CorrelationId = Guid.NewGuid().ToString(),
        CausationId = Guid.NewGuid().ToString()
      },
      Scope = new PerspectiveScope(),
      CreatedAt = now,
      UpdatedAt = now,
      Version = 1
    };
  }
}
