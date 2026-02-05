namespace Whizbang.Transports.HotChocolate.Generators;

/// <summary>
/// Value type containing information about a discovered [GraphQLLens] decorated type.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="InterfaceName">Fully qualified interface name (e.g., "global::MyApp.Lenses.IOrderLens")</param>
/// <param name="ModelTypeName">Fully qualified model type name (e.g., "global::MyApp.ReadModels.OrderReadModel")</param>
/// <param name="QueryName">GraphQL query field name (e.g., "orders")</param>
/// <param name="Scope">Scope flags value as integer</param>
/// <param name="EnableFiltering">Whether filtering is enabled</param>
/// <param name="EnableSorting">Whether sorting is enabled</param>
/// <param name="EnablePaging">Whether paging is enabled</param>
/// <param name="EnableProjection">Whether projection is enabled</param>
/// <param name="DefaultPageSize">Default page size for paging</param>
/// <param name="MaxPageSize">Maximum page size for paging</param>
/// <param name="Namespace">The namespace for generated code</param>
internal sealed record GraphQLLensInfo(
    string InterfaceName,
    string ModelTypeName,
    string QueryName,
    int Scope,
    bool EnableFiltering,
    bool EnableSorting,
    bool EnablePaging,
    bool EnableProjection,
    int DefaultPageSize,
    int MaxPageSize,
    string Namespace
);
