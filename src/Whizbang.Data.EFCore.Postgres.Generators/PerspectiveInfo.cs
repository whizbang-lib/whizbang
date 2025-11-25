namespace Whizbang.Data.EFCore.Postgres.Generators;

/// <summary>
/// Value type containing information about a discovered perspective.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="ModelTypeName">Fully qualified model type name (e.g., "global::MyApp.Orders.OrderSummary")</param>
/// <param name="TableName">PostgreSQL table name for this perspective</param>
internal sealed record PerspectiveInfo(
    string ModelTypeName,
    string TableName
);
