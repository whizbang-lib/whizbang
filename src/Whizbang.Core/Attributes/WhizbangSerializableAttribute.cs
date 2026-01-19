namespace Whizbang;

/// <summary>
/// Marks a type for automatic JSON serialization context generation.
/// Types marked with this attribute will be included in the assembly's generated MessageJsonContext.
/// </summary>
/// <remarks>
/// Use this attribute for types that need JSON serialization but don't implement ICommand or IEvent:
/// <list type="bullet">
/// <item><description>Perspective lens DTOs (read models)</description></item>
/// <item><description>API response models</description></item>
/// <item><description>JSONB column types</description></item>
/// <item><description>Value objects</description></item>
/// </list>
/// <para>
/// The MessageJsonContextGenerator will discover these types at build time and include them
/// in the auto-generated JsonSerializerContext with module initializer registration.
/// </para>
/// <example>
/// <code>
/// [WhizbangSerializable]
/// public record ProductDto {
///   public required Guid ProductId { get; init; }
///   public required string Name { get; init; }
///   public required decimal Price { get; init; }
/// }
/// </code>
/// </example>
/// </remarks>
/// <docs>source-generators/json-contexts#serializing-additional-types</docs>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class WhizbangSerializableAttribute : Attribute {
}
