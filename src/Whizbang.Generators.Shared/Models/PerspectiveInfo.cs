namespace Whizbang.Generators.Shared.Models;

/// <summary>
/// Information about a discovered perspective.
/// This is a sealed record using value equality, which is critical for
/// incremental generator caching performance.
/// </summary>
/// <param name="HandlerType">
/// Fully-qualified handler class name (e.g., "global::MyApp.ProductPerspective").
/// Nullable when discovered from DbSet property only.
/// </param>
/// <param name="EventType">
/// Fully-qualified event type name (e.g., "global::MyApp.Events.ProductCreated").
/// Nullable when discovered from DbSet property only.
/// </param>
/// <param name="StateType">
/// Fully-qualified state/model type name (e.g., "global::MyApp.ProductDto").
/// Always present - this is the core perspective model type.
/// </param>
/// <param name="TableName">
/// Database table name (e.g., "product_dtos" or "Products").
/// Convention: snake_case from type name, or property name from DbSet.
/// </param>
/// <param name="StreamKeyType">
/// Fully-qualified stream key type for aggregate perspectives (e.g., "global::MyApp.ProductId").
/// Nullable for non-aggregate perspectives.
/// </param>
public sealed record PerspectiveInfo(
  string? HandlerType,
  string? EventType,
  string StateType,
  string TableName,
  string? StreamKeyType = null
);
