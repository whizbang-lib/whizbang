namespace Whizbang.Transports.FastEndpoints.Generators;

/// <summary>
/// Value type containing information about a discovered [RestLens] decorated type.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="InterfaceName">Fully qualified interface name (e.g., "global::MyApp.Lenses.IOrderLens")</param>
/// <param name="ModelTypeName">Fully qualified model type name (e.g., "global::MyApp.ReadModels.OrderReadModel")</param>
/// <param name="Route">REST route (e.g., "/api/orders")</param>
/// <param name="EnableFiltering">Whether filtering is enabled</param>
/// <param name="EnableSorting">Whether sorting is enabled</param>
/// <param name="EnablePaging">Whether paging is enabled</param>
/// <param name="DefaultPageSize">Default page size for paging</param>
/// <param name="MaxPageSize">Maximum page size for paging</param>
/// <param name="Namespace">The namespace for generated code</param>
/// <param name="EndpointClassName">Generated endpoint class name</param>
internal sealed record RestLensInfo(
    string InterfaceName,
    string ModelTypeName,
    string Route,
    bool EnableFiltering,
    bool EnableSorting,
    bool EnablePaging,
    int DefaultPageSize,
    int MaxPageSize,
    string Namespace,
    string EndpointClassName
);
