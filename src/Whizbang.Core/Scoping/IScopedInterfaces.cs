namespace Whizbang.Core.Scoping;

/// <summary>
/// Marker interface for models that contain TenantId in the DATA model.
/// Optional - use when tenant ID is part of business data, not just infrastructure.
/// </summary>
/// <docs>core-concepts/scoping#marker-interfaces</docs>
/// <tests>Whizbang.Core.Tests/Scoping/MarkerInterfaceTests.cs</tests>
/// <remarks>
/// <para>
/// This is different from PerspectiveScope.TenantId which is stored in the scope column.
/// Use this interface when the tenant ID is also needed in the actual data model.
/// </para>
/// </remarks>
/// <example>
/// public class Order : ITenantScoped {
///   public string TenantId { get; init; }
///   public string OrderNumber { get; init; }
///   // ...
/// }
/// </example>
public interface ITenantScoped {
  /// <summary>
  /// The tenant identifier for this model.
  /// </summary>
  string TenantId { get; }
}

/// <summary>
/// Marker interface for models scoped to both tenant and user.
/// </summary>
/// <docs>core-concepts/scoping#marker-interfaces</docs>
/// <tests>Whizbang.Core.Tests/Scoping/MarkerInterfaceTests.cs</tests>
public interface IUserScoped : ITenantScoped {
  /// <summary>
  /// The user identifier for this model.
  /// </summary>
  string UserId { get; }
}

/// <summary>
/// Marker interface for models scoped to organization.
/// </summary>
/// <docs>core-concepts/scoping#marker-interfaces</docs>
/// <tests>Whizbang.Core.Tests/Scoping/MarkerInterfaceTests.cs</tests>
public interface IOrganizationScoped : ITenantScoped {
  /// <summary>
  /// The organization identifier for this model.
  /// </summary>
  string OrganizationId { get; }
}

/// <summary>
/// Marker interface for models scoped to customer.
/// </summary>
/// <docs>core-concepts/scoping#marker-interfaces</docs>
/// <tests>Whizbang.Core.Tests/Scoping/MarkerInterfaceTests.cs</tests>
public interface ICustomerScoped : ITenantScoped {
  /// <summary>
  /// The customer identifier for this model.
  /// </summary>
  string CustomerId { get; }
}
