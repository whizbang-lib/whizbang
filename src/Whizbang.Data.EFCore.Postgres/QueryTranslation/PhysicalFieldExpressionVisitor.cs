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
/// <docs>perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/UnifiedQuerySyntaxTests.cs</tests>
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

    // Check if this is a property access
    if (node.Member is not PropertyInfo propertyInfo) {
      return base.VisitMember(node);
    }

    // Check if the expression is accessing a property through .Data
    // i.e., node.Expression is also a MemberExpression for "Data"
    if (node.Expression is MemberExpression dataAccess &&
        dataAccess.Member.Name == "Data" &&
        _isPerspectiveRowType(dataAccess.Expression?.Type)) {

      // Get the model type (TModel from PerspectiveRow<TModel>)
      var modelType = propertyInfo.DeclaringType;
      if (modelType == null) {
        return base.VisitMember(node);
      }

      // Check if this property is registered as a physical field
      if (PhysicalFieldRegistry.TryGetMapping(modelType, propertyInfo.Name, out var mapping)) {
        // Rewrite to: EF.Property<TProperty>(r, "shadow_property_name")
        // Where r is the PerspectiveRow parameter (dataAccess.Expression)

        var entityExpression = dataAccess.Expression;
        if (entityExpression == null) {
          return base.VisitMember(node);
        }

        // Visit the entity expression in case it needs transformation
        var visitedEntity = Visit(entityExpression);

        // Create EF.Property<TProperty>(entity, "shadow_property_name")
        var efPropertyGeneric = _efPropertyMethod.MakeGenericMethod(propertyInfo.PropertyType);
        var columnNameConstant = Expression.Constant(mapping.ShadowPropertyName);

        return Expression.Call(null, efPropertyGeneric, visitedEntity, columnNameConstant);
      }
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
