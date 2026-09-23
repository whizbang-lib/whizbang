using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres.QueryTranslation;

/// <summary>
/// Expression visitor that rewrites r.Data.PropertyName to EF.Property(r, "shadow_property")
/// for properties registered as physical fields.
/// </summary>
/// <remarks>
/// <para>
/// This visitor enables unified query syntax by intercepting member access expressions
/// on the Data property of PerspectiveRow&lt;TModel&gt; and redirecting physical field
/// access to the corresponding shadow property.
/// </para>
/// <para>
/// Before transformation:
/// <code>
/// .Where(r => r.Data.Price >= 50.00m)
/// </code>
/// </para>
/// <para>
/// After transformation:
/// <code>
/// .Where(r => EF.Property&lt;decimal&gt;(r, "price") >= 50.00m)
/// </code>
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnifiedQuerySyntaxTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SplitModeProductionTests.cs</tests>
[SuppressMessage("AOT", "IL2060:MakeGenericMethod can break functionality when AOT compiling", Justification = "EF Core data layer inherently uses reflection for query translation")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "EF Core data layer inherently uses reflection for query translation")]
public class PhysicalFieldExpressionVisitor : ExpressionVisitor {
  // Cache the EF.Property<T> method info
  private static readonly MethodInfo _efPropertyMethod =
      typeof(EF).GetMethod(nameof(EF.Property))!;

  /// <summary>
  /// Visits a member access expression and rewrites physical field access.
  /// </summary>
  protected override Expression VisitMember(MemberExpression node) {
    // We're looking for: r.Data.PropertyName
    // Where r is PerspectiveRow<TModel>, Data is the JSONB property, PropertyName is on TModel
    //
    // Every condition is one expression so there is a single "leave the node alone" exit. Written
    // as separate guards, the ones nothing can falsify — a member access always has a declaring
    // type, and matching PerspectiveRow<> above already proves the entity expression is there —
    // each grew their own unreachable `return base.VisitMember(node)`.
    if (node.Member is PropertyInfo propertyInfo &&
        node.Expression is MemberExpression { Member.Name: "Data", Expression: { } entityExpression } &&
        _isPerspectiveRowType(entityExpression.Type) &&
        // The model type is TModel from PerspectiveRow<TModel>, as the property declares it.
        propertyInfo.DeclaringType is { } modelType &&
        // Only properties registered as physical fields are rewritten.
        PhysicalFieldRegistry.TryGetMapping(modelType, propertyInfo.Name, out var mapping)) {
      // Rewrite to: EF.Property<TProperty>(r, "shadow_property_name")
      // Where r is the PerspectiveRow parameter (entityExpression)

      // Visit the entity expression in case it needs transformation
      var visitedEntity = Visit(entityExpression);

      if (!mapping.IsVector) {
        // Non-vector: direct EF.Property<TProperty>(r, "shadow_property_name")
        var efPropertyGeneric = _efPropertyMethod.MakeGenericMethod(propertyInfo.PropertyType);
        var columnNameConstant = Expression.Constant(mapping.ShadowPropertyName);
        return Expression.Call(null, efPropertyGeneric, visitedEntity, columnNameConstant);
      }

      // Vector fields: DO NOT rewrite member access.
      //
      // The shadow property is Vector? but the model property is float[]? — EF Core
      // cannot coerce between these types in expression trees. Rewriting causes:
      //   "No coercion operator is defined between types 'String' and 'Single[]'"
      //   or "Expression of type 'Vector' cannot be used for return type 'Single[]'"
      //
      // Full entity materialization works via ChangeTracker hydration (SplitModeChangeTrackerHydrator).
      // For Select projections, use EF.Property<Vector?>(r, "embeddings") explicitly.
      // The Whizbang vector extensions (OrderByCosineDistance, etc.) handle their own rewriting.
      //
      // Unifying the shadow property type to float[] waits on the vector extension refactor.
    }

    return base.VisitMember(node);
  }

  /// <summary>
  /// Checks if a type is PerspectiveRow&lt;T&gt; or derives from it.
  /// </summary>
  private static bool _isPerspectiveRowType(Type? type) {
    if (type == null) {
      return false;
    }

    // Check if it's a generic type based on PerspectiveRow<>
    if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PerspectiveRow<>)) {
      return true;
    }

    // Check base types
    var baseType = type.BaseType;
    while (baseType != null) {
      if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == typeof(PerspectiveRow<>)) {
        return true;
      }

      baseType = baseType.BaseType;
    }

    return false;
  }
}
