using System.Reflection;
using HotChocolate.Types;
using HotChocolate.Types.Descriptors;

namespace Whizbang.Transports.HotChocolate.Middleware;

/// <summary>
/// Attribute that applies the OrderByStripping middleware to strip pre-existing
/// OrderBy expressions before HotChocolate's sorting middleware runs.
/// </summary>
/// <remarks>
/// <para>
/// Apply this attribute AFTER [UseSorting] in the attribute stack (closer to resolver)
/// to ensure pre-existing ordering is stripped before GraphQL sorting is applied.
/// </para>
/// <example>
/// <code>
/// [UsePaging]
/// [UseFiltering]
/// [UseSorting]
/// [UseOrderByStripping]  // Must be after UseSorting
/// public IQueryable&lt;Order&gt; GetOrders() => _orders.Query;
/// </code>
/// </example>
/// </remarks>
/// <docs>graphql/sorting</docs>
/// <tests>Whizbang.Transports.HotChocolate.Tests/Integration/QueryExecutionTests.cs</tests>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false)]
public sealed class UseOrderByStrippingAttribute : ObjectFieldDescriptorAttribute {
  /// <summary>
  /// Applies the middleware to the field descriptor.
  /// </summary>
  protected override void OnConfigure(
      IDescriptorContext context,
      IObjectFieldDescriptor descriptor,
      MemberInfo member) {
    descriptor.Use(OrderByStrippingMiddleware.Create());
  }
}
