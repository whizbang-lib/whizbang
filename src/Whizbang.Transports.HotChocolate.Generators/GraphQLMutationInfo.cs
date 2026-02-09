namespace Whizbang.Transports.HotChocolate.Generators;

/// <summary>
/// Value type for caching GraphQL mutation information during incremental generation.
/// Using sealed record ensures proper value equality for pipeline caching.
/// </summary>
/// <param name="CommandTypeName">Fully qualified command type name.</param>
/// <param name="CommandTypeNameShort">Short command type name for display.</param>
/// <param name="ResultTypeName">Fully qualified result type name.</param>
/// <param name="ResultTypeNameShort">Short result type name for display.</param>
/// <param name="GraphQLMutationName">The GraphQL mutation field name (e.g., "createOrder").</param>
/// <param name="RequestTypeName">Optional custom request type, null if command is used directly.</param>
/// <param name="Namespace">The namespace to generate code in.</param>
/// <param name="MutationClassName">The generated mutation class name (e.g., "CreateOrderCommandMutation").</param>
internal sealed record GraphQLMutationInfo(
    string CommandTypeName,
    string CommandTypeNameShort,
    string ResultTypeName,
    string ResultTypeNameShort,
    string GraphQLMutationName,
    string? RequestTypeName,
    string Namespace,
    string MutationClassName
);
