using System.Linq.Expressions;
using Whizbang.Core.Lenses;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// Extension methods for querying polymorphic types in perspective models.
/// Provides type-safe fluent API for filtering by derived types using physical discriminator columns.
/// </summary>
/// <remarks>
/// <para>
/// The polymorphic query API filters by discriminator values stored in indexed database columns.
/// Use [PolymorphicDiscriminator] attribute on a string property that stores the type name.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/polymorphic-types</docs>
/// <example>
/// <code>
/// // Query via discriminator property with type-safe helper
/// var results = await query
///     .WhereDiscriminatorEquals&lt;TextFieldSettings&gt;(m => m.SettingsTypeName)
///     .ToListAsync();
///
/// // Or query directly (full SQL, indexed)
/// var results = await query
///     .Where(r => r.Data.SettingsTypeName == nameof(TextFieldSettings))
///     .ToListAsync();
/// </code>
/// </example>
#pragma warning disable S1144 // All private members are used; WhereDiscriminatorEquals is a public extension method
public static class PolymorphicQueryExtensions {
#pragma warning restore S1144
  /// <summary>
  /// Filters rows where the discriminator property equals the type name of TDerived.
  /// Uses the indexed physical discriminator column for efficient queries.
  /// </summary>
  /// <typeparam name="TModel">The perspective model type.</typeparam>
  /// <typeparam name="TDerived">The derived type to filter by.</typeparam>
  /// <param name="query">The queryable to filter.</param>
  /// <param name="discriminatorSelector">Lambda selecting the discriminator property (marked with [PolymorphicDiscriminator]).</param>
  /// <returns>A filtered queryable containing only rows matching the derived type.</returns>
  /// <remarks>
  /// <para>
  /// The discriminator value is derived from <c>typeof(TDerived).Name</c>.
  /// This provides a type-safe way to query by polymorphic type without magic strings.
  /// </para>
  /// <para>
  /// For full type name matching, use <see cref="WhereDiscriminatorEqualsFullName{TModel, TDerived}"/>.
  /// </para>
  /// </remarks>
  /// <example>
  /// <code>
  /// // Filter by type name
  /// var results = await query
  ///     .WhereDiscriminatorEquals&lt;MyModel, TextFieldSettings&gt;(m => m.SettingsTypeName)
  ///     .ToListAsync();
  /// </code>
  /// </example>
  public static IQueryable<PerspectiveRow<TModel>> WhereDiscriminatorEquals<TModel, TDerived>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, string>> discriminatorSelector)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(discriminatorSelector);

    var typeName = typeof(TDerived).Name;
    return query.WhereDiscriminatorValue(discriminatorSelector, typeName);
  }

  /// <summary>
  /// Filters rows where the discriminator property equals the full type name of TDerived.
  /// Uses the indexed physical discriminator column for efficient queries.
  /// </summary>
  /// <typeparam name="TModel">The perspective model type.</typeparam>
  /// <typeparam name="TDerived">The derived type to filter by.</typeparam>
  /// <param name="query">The queryable to filter.</param>
  /// <param name="discriminatorSelector">Lambda selecting the discriminator property (marked with [PolymorphicDiscriminator]).</param>
  /// <returns>A filtered queryable containing only rows matching the derived type.</returns>
  /// <remarks>
  /// The discriminator value is derived from <c>typeof(TDerived).FullName</c>.
  /// Use this when discriminator values store fully-qualified type names.
  /// </remarks>
  public static IQueryable<PerspectiveRow<TModel>> WhereDiscriminatorEqualsFullName<TModel, TDerived>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, string>> discriminatorSelector)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(discriminatorSelector);

    var typeName = typeof(TDerived).FullName ?? typeof(TDerived).Name;
    return query.WhereDiscriminatorValue(discriminatorSelector, typeName);
  }

  /// <summary>
  /// Filters rows where the discriminator property equals the specified value.
  /// Uses the indexed physical discriminator column for efficient queries.
  /// </summary>
  /// <typeparam name="TModel">The perspective model type.</typeparam>
  /// <param name="query">The queryable to filter.</param>
  /// <param name="discriminatorSelector">Lambda selecting the discriminator property.</param>
  /// <param name="discriminatorValue">The discriminator value to match.</param>
  /// <returns>A filtered queryable containing only matching rows.</returns>
  /// <remarks>
  /// This is the base method used by type-safe helpers. Use this directly when
  /// the discriminator value is known at runtime or stored differently.
  /// </remarks>
  public static IQueryable<PerspectiveRow<TModel>> WhereDiscriminatorValue<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, string>> discriminatorSelector,
      string discriminatorValue)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(discriminatorSelector);
    ArgumentNullException.ThrowIfNull(discriminatorValue);

    // Build: r => r.Data.DiscriminatorProperty == discriminatorValue
    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");

    // Access r.Data using AOT-safe property access
    var dataProperty = _getDataProperty<TModel>();
    var dataAccess = Expression.MakeMemberAccess(param, dataProperty);

    // Apply the discriminator selector to get the property value
    var discriminatorAccess = Expression.Invoke(discriminatorSelector, dataAccess);

    // Build equality comparison: discriminatorProperty == discriminatorValue
    // Wrap value in closure for proper parameterization
    var valueHolder = new StringValueHolder { Value = discriminatorValue };
    var valueExpr = Expression.MakeMemberAccess(
        Expression.Constant(valueHolder),
        _stringValueHolderProperty);

    var comparison = Expression.Equal(discriminatorAccess, valueExpr);
    var filterLambda = Expression.Lambda<Func<PerspectiveRow<TModel>, bool>>(comparison, param);

    return query.Where(filterLambda);
  }

  /// <summary>
  /// Filters rows where the discriminator property is one of the specified values.
  /// Uses the indexed physical discriminator column for efficient queries.
  /// </summary>
  /// <typeparam name="TModel">The perspective model type.</typeparam>
  /// <param name="query">The queryable to filter.</param>
  /// <param name="discriminatorSelector">Lambda selecting the discriminator property.</param>
  /// <param name="discriminatorValues">The discriminator values to match (OR condition).</param>
  /// <returns>A filtered queryable containing only matching rows.</returns>
  public static IQueryable<PerspectiveRow<TModel>> WhereDiscriminatorIn<TModel>(
      this IQueryable<PerspectiveRow<TModel>> query,
      Expression<Func<TModel, string>> discriminatorSelector,
      params string[] discriminatorValues)
      where TModel : class {
    ArgumentNullException.ThrowIfNull(discriminatorSelector);
    ArgumentNullException.ThrowIfNull(discriminatorValues);

    if (discriminatorValues.Length == 0) {
      // No values = no matches
      return query.Where(_ => false);
    }

    if (discriminatorValues.Length == 1) {
      return query.WhereDiscriminatorValue(discriminatorSelector, discriminatorValues[0]);
    }

    // Build: r => values.Contains(r.Data.DiscriminatorProperty)
    var param = Expression.Parameter(typeof(PerspectiveRow<TModel>), "r");

    // Access r.Data
    var dataProperty = _getDataProperty<TModel>();
    var dataAccess = Expression.MakeMemberAccess(param, dataProperty);

    // Apply the discriminator selector
    var discriminatorAccess = Expression.Invoke(discriminatorSelector, dataAccess);

    // Build Contains call using cached method
    var valuesHolder = new StringArrayHolder { Values = discriminatorValues };
    var valuesExpr = Expression.MakeMemberAccess(
        Expression.Constant(valuesHolder),
        _stringArrayHolderProperty);

    var containsCall = Expression.Call(_containsMethod, valuesExpr, discriminatorAccess);
    var filterLambda = Expression.Lambda<Func<PerspectiveRow<TModel>, bool>>(containsCall, param);

    return query.Where(filterLambda);
  }

  // AOT-safe: Helper to get Data property using compile-time expression
  private static System.Reflection.PropertyInfo _getDataProperty<TModel>() where TModel : class {
    Expression<Func<PerspectiveRow<TModel>, TModel>> expr = r => r.Data;
    var memberExpr = (MemberExpression)expr.Body;
    return (System.Reflection.PropertyInfo)memberExpr.Member;
  }

  // AOT-safe: Cache Contains method info
  private static readonly System.Reflection.MethodInfo _containsMethod =
      _getContainsMethod();

  private static System.Reflection.MethodInfo _getContainsMethod() {
    // Use Enumerable.Contains explicitly to avoid MemoryExtensions.Contains(ReadOnlySpan<T>)
    Expression<Func<IEnumerable<string>, string, bool>> expr = (arr, val) => Enumerable.Contains(arr, val);
    var callExpr = (MethodCallExpression)expr.Body;
    return callExpr.Method;
  }

  // Value holders for parameterization (EF Core parameterizes member access on constant objects)
  private sealed class StringValueHolder {
    public string Value { get; set; } = null!;
  }

  private sealed class StringArrayHolder {
    public string[] Values { get; set; } = null!;
  }

  // AOT-safe: Cache property info for value holders
  private static readonly System.Reflection.PropertyInfo _stringValueHolderProperty =
      _getStringValueHolderProperty();

  private static readonly System.Reflection.PropertyInfo _stringArrayHolderProperty =
      _getStringArrayHolderProperty();

  private static System.Reflection.PropertyInfo _getStringValueHolderProperty() {
    Expression<Func<StringValueHolder, string>> expr = h => h.Value;
    var memberExpr = (MemberExpression)expr.Body;
    return (System.Reflection.PropertyInfo)memberExpr.Member;
  }

  private static System.Reflection.PropertyInfo _getStringArrayHolderProperty() {
    Expression<Func<StringArrayHolder, string[]>> expr = h => h.Values;
    var memberExpr = (MemberExpression)expr.Body;
    return (System.Reflection.PropertyInfo)memberExpr.Member;
  }
}
