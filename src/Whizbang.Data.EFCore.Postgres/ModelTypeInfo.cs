namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Value type containing metadata about a perspective model type discovered during source generation.
/// Used by EFCoreServiceRegistrationGenerator to generate registration metadata at compile time.
/// </summary>
/// <param name="Type">The CLR type of the perspective model (e.g., typeof(OrderReadModel)).</param>
/// <param name="TableName">The database table name for this perspective (e.g., "order_read_model").</param>
/// <example>
/// Generated usage:
/// <code>
/// internal static class EFCoreRegistrationMetadata {
///     internal static readonly ModelTypeInfo[] Models = [
///         new(typeof(global::MyApp.OrderReadModel), "order_read_model"),
///         new(typeof(global::MyApp.ProductDto), "product_dto")
///     ];
/// }
/// </code>
/// </example>
public sealed record ModelTypeInfo(Type Type, string TableName);
