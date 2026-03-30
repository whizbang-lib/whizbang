using System.Text.Json.Serialization;
using HotChocolate.Execution.Configuration;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Extension methods for registering polymorphic types with HotChocolate GraphQL.
/// Automatically discovers [JsonPolymorphic] types and registers them as GraphQL interfaces.
/// </summary>
/// <docs>apis/graphql/polymorphic-types</docs>
public static class PolymorphicTypeExtensions {
  /// <summary>
  /// Registers a polymorphic type hierarchy with HotChocolate GraphQL.
  /// The base type becomes a GraphQL interface, and all derived types are registered
  /// as implementations. Types are discovered from [JsonDerivedType] attributes.
  /// </summary>
  /// <typeparam name="TBase">The abstract base type with [JsonPolymorphic] attribute.</typeparam>
  /// <param name="builder">The HotChocolate request executor builder.</param>
  /// <param name="derivedTypes">The concrete derived types to register.</param>
  /// <returns>The builder for chaining.</returns>
  /// <remarks>
  /// <para>
  /// This method enables turn-key GraphQL support for polymorphic types that use
  /// System.Text.Json's [JsonPolymorphic] and [JsonDerivedType] attributes.
  /// </para>
  /// <para>
  /// The base type is registered as a GraphQL InterfaceType, and each derived type
  /// is registered as an ObjectType that implements the interface.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Register polymorphic field settings type hierarchy
  /// services.AddGraphQLServer()
  ///     .AddWhizbangLenses()
  ///     .AddPolymorphicType&lt;AbstractFieldSettings&gt;(
  ///         typeof(TextFieldSettings),
  ///         typeof(NumberFieldSettings),
  ///         typeof(DateFieldSettings));
  /// </code>
  /// </example>
  /// <docs>apis/graphql/polymorphic-types</docs>
  public static IRequestExecutorBuilder AddPolymorphicType<TBase>(
      this IRequestExecutorBuilder builder,
      params Type[] derivedTypes) where TBase : class {

    // Register base type as interface
    builder.AddInterfaceType<TBase>();

    // Register each derived type and bind to interface
    foreach (var derivedType in derivedTypes) {
      builder.AddType(derivedType);
    }

    return builder;
  }

  /// <summary>
  /// Registers a polymorphic type hierarchy by discovering derived types from
  /// [JsonDerivedType] attributes on the base type.
  /// </summary>
  /// <typeparam name="TBase">The abstract base type with [JsonPolymorphic] and [JsonDerivedType] attributes.</typeparam>
  /// <param name="builder">The HotChocolate request executor builder.</param>
  /// <returns>The builder for chaining.</returns>
  /// <remarks>
  /// <para>
  /// This method automatically discovers derived types from [JsonDerivedType] attributes
  /// on the base type. The base type must have [JsonPolymorphic] attribute and at least
  /// one [JsonDerivedType] attribute.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Base type with JsonDerivedType attributes
  /// [JsonPolymorphic]
  /// [JsonDerivedType(typeof(TextFieldSettings))]
  /// [JsonDerivedType(typeof(NumberFieldSettings))]
  /// public abstract class AbstractFieldSettings { }
  ///
  /// // Register - derived types auto-discovered
  /// services.AddGraphQLServer()
  ///     .AddWhizbangLenses()
  ///     .AddPolymorphicType&lt;AbstractFieldSettings&gt;();
  /// </code>
  /// </example>
  /// <docs>apis/graphql/polymorphic-types</docs>
  public static IRequestExecutorBuilder AddPolymorphicType<TBase>(
      this IRequestExecutorBuilder builder) where TBase : class {

    var baseType = typeof(TBase);

    // Verify base type has [JsonPolymorphic]
    var polymorphicAttr = baseType.GetCustomAttributes(typeof(JsonPolymorphicAttribute), false);
    if (polymorphicAttr.Length == 0) {
      throw new InvalidOperationException(
          $"Type '{baseType.Name}' must have [JsonPolymorphic] attribute to use AddPolymorphicType.");
    }

    // Discover derived types from [JsonDerivedType] attributes
    var derivedTypeAttrs = baseType.GetCustomAttributes(typeof(JsonDerivedTypeAttribute), false)
        .Cast<JsonDerivedTypeAttribute>()
        .ToList();

    if (derivedTypeAttrs.Count == 0) {
      throw new InvalidOperationException(
          $"Type '{baseType.Name}' must have at least one [JsonDerivedType] attribute to use AddPolymorphicType.");
    }

    var derivedTypes = derivedTypeAttrs.Select(a => a.DerivedType).ToArray();

    return builder.AddPolymorphicType<TBase>(derivedTypes);
  }
}
