namespace Whizbang.Transports.FastEndpoints.Generators;

/// <summary>
/// Represents information about a command endpoint marked with [CommandEndpoint] attribute.
/// This is a value type (sealed record) for efficient incremental generator caching.
/// Used by <see cref="RestMutationEndpointGenerator"/> during code generation.
/// </summary>
/// <remarks>
/// Sealed record ensures value equality semantics and is optimized for
/// incremental generator pipelines per Roslyn best practices.
/// </remarks>
/// <param name="CommandTypeName">The fully qualified name of the command type (e.g., "TestApp.CreateOrderCommand").</param>
/// <param name="CommandTypeNameShort">The short name of the command type without namespace (e.g., "CreateOrderCommand").</param>
/// <param name="ResultTypeName">The fully qualified name of the result type (e.g., "TestApp.OrderResult").</param>
/// <param name="ResultTypeNameShort">The short name of the result type without namespace (e.g., "OrderResult").</param>
/// <param name="RestRoute">The REST route for the endpoint (e.g., "/api/orders").</param>
/// <param name="RequestTypeName">Optional custom request DTO type name, or null if command is used directly.</param>
/// <param name="Namespace">The namespace where the command is defined.</param>
/// <param name="EndpointClassName">The generated endpoint class name (e.g., "CreateOrderCommandEndpoint").</param>
internal sealed record RestMutationInfo(
    string CommandTypeName,
    string CommandTypeNameShort,
    string ResultTypeName,
    string ResultTypeNameShort,
    string RestRoute,
    string? RequestTypeName,
    string Namespace,
    string EndpointClassName
);
