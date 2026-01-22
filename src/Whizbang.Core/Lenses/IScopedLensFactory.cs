namespace Whizbang.Core.Lenses;

/// <summary>
/// Factory for creating scoped lens instances with pre-applied filters.
/// Enables tenant/user-scoped queries without manual WHERE clauses.
/// </summary>
/// <remarks>
/// <para>
/// The factory pattern allows runtime scope selection while maintaining
/// compile-time type safety. Scopes are defined at registration via
/// <see cref="LensOptions.DefineScope"/> and resolved at runtime.
/// </para>
/// <para>
/// Use this factory when different request contexts need different
/// data filtering (e.g., tenant isolation, user-specific data).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Get tenant-scoped lens
/// var lens = _lensFactory.GetLens&lt;IOrderLens&gt;("Tenant");
/// var orders = await lens.QueryAsync(q => q.Where(o => o.Status == Active), ct);
/// // Query is pre-filtered by tenant - no manual WHERE needed
/// </code>
/// </example>
/// <docs>core-concepts/scoped-lenses#factory-pattern</docs>
/// <tests>Whizbang.Core.Tests/Lenses/ScopedLensFactoryTests.cs</tests>
public interface IScopedLensFactory {
  /// <summary>
  /// Gets a lens instance with the specified scope filter applied.
  /// </summary>
  /// <typeparam name="TLens">The lens interface type (must implement ILensQuery).</typeparam>
  /// <param name="scopeName">The scope name as defined in LensOptions.DefineScope().</param>
  /// <returns>A lens instance with the scope filter pre-applied.</returns>
  /// <exception cref="ArgumentException">Thrown when scopeName is not defined.</exception>
  /// <example>
  /// <code>
  /// // Get tenant-scoped lens
  /// var tenantLens = factory.GetLens&lt;IOrderLens&gt;("Tenant");
  ///
  /// // Get user-scoped lens
  /// var userLens = factory.GetLens&lt;IOrderLens&gt;("User");
  ///
  /// // Get global lens (no filter)
  /// var globalLens = factory.GetLens&lt;IOrderLens&gt;("Global");
  /// </code>
  /// </example>
  TLens GetLens<TLens>(string scopeName) where TLens : ILensQuery;
}
