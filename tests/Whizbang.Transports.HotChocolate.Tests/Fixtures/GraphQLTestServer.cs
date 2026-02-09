using HotChocolate;
using HotChocolate.Data;
using HotChocolate.Execution;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Lenses;

namespace Whizbang.Transports.HotChocolate.Tests.Fixtures;

/// <summary>
/// Base query type for integration tests.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "HotChocolate requires instance methods for GraphQL resolvers")]
public class Query {
  /// <summary>
  /// Query orders from the test lens.
  /// </summary>
  [UsePaging(DefaultPageSize = 10, MaxPageSize = 100, IncludeTotalCount = true)]
  [UseProjection]
  [UseFiltering]
  [UseSorting]
  public IQueryable<PerspectiveRow<OrderReadModel>> GetOrders(
      [Service] IOrderLens lens) {
    return lens.Query;
  }

  /// <summary>
  /// Query products from the test lens.
  /// </summary>
  [UsePaging(DefaultPageSize = 10, MaxPageSize = 50, IncludeTotalCount = true)]
  [UseProjection]
  [UseFiltering]
  [UseSorting]
  public IQueryable<PerspectiveRow<ProductReadModel>> GetProducts(
      [Service] IProductLens lens) {
    return lens.Query;
  }

  /// <summary>
  /// Query with filtering only (no paging/sorting).
  /// </summary>
  [UseFiltering]
  public IQueryable<PerspectiveRow<OrderReadModel>> GetFilteredItems(
      [Service] IFilterOnlyLens lens) {
    return lens.Query;
  }
}

/// <summary>
/// GraphQL test server for integration testing.
/// Provides a configured HotChocolate executor for running GraphQL queries.
/// </summary>
public sealed class GraphQLTestServer : IAsyncDisposable {
  private readonly IRequestExecutor _executor;
  private readonly ServiceProvider _serviceProvider;

  public TestOrderLens OrderLens { get; }
  public TestProductLens ProductLens { get; }
  public TestFilterOnlyLens FilterOnlyLens { get; }

  private GraphQLTestServer(
      IRequestExecutor executor,
      ServiceProvider serviceProvider,
      TestOrderLens orderLens,
      TestProductLens productLens,
      TestFilterOnlyLens filterOnlyLens) {
    _executor = executor;
    _serviceProvider = serviceProvider;
    OrderLens = orderLens;
    ProductLens = productLens;
    FilterOnlyLens = filterOnlyLens;
  }

  /// <summary>
  /// Creates a new GraphQL test server instance.
  /// </summary>
  public static async Task<GraphQLTestServer> CreateAsync() {
    var orderLens = new TestOrderLens();
    var productLens = new TestProductLens();
    var filterOnlyLens = new TestFilterOnlyLens();

    var services = new ServiceCollection();

    // Register test lenses
    services.AddSingleton<IOrderLens>(orderLens);
    services.AddSingleton<IProductLens>(productLens);
    services.AddSingleton<IFilterOnlyLens>(filterOnlyLens);

    // Configure HotChocolate
    services
        .AddGraphQLServer()
        .AddWhizbangLenses()
        .AddQueryType<Query>();

    var serviceProvider = services.BuildServiceProvider();
    var executor = await serviceProvider.GetRequestExecutorAsync();

    return new GraphQLTestServer(
        executor,
        serviceProvider,
        orderLens,
        productLens,
        filterOnlyLens);
  }

  /// <summary>
  /// Executes a GraphQL query and returns the result.
  /// </summary>
  public async Task<IExecutionResult> ExecuteAsync(string query) {
    return await _executor.ExecuteAsync(query);
  }

  /// <summary>
  /// Gets the schema for inspection.
  /// </summary>
  public ISchema Schema => _executor.Schema;

  public async ValueTask DisposeAsync() {
    await _serviceProvider.DisposeAsync();
  }
}
