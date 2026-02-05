namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Flags enum determining which parts of <see cref="Whizbang.Core.Lenses.PerspectiveRow{TModel}"/>
/// are exposed in GraphQL queries. Flags can be combined for fine-grained control.
/// </summary>
/// <docs>v0.1.0/graphql/lens-integration#scope</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/GraphQLLensScopeTests.cs</tests>
/// <example>
/// // Expose only the model data (simplest, most common)
/// [GraphQLLens(Scope = GraphQLLensScope.DataOnly)]
///
/// // Expose data plus system fields (Id, CreatedAt, UpdatedAt, Version)
/// [GraphQLLens(Scope = GraphQLLensScope.Data | GraphQLLensScope.SystemFields)]
///
/// // Expose everything for admin queries
/// [GraphQLLens(Scope = GraphQLLensScope.All)]
///
/// // Use system-configured default
/// [GraphQLLens(Scope = GraphQLLensScope.Default)]
/// </example>
[Flags]
public enum GraphQLLensScope {
  /// <summary>
  /// Use system-configured default from <see cref="WhizbangGraphQLOptions.DefaultScope"/>.
  /// When no explicit scope is set, the system default is applied at runtime.
  /// </summary>
  Default = 0,

  /// <summary>
  /// Expose the <see cref="Whizbang.Core.Lenses.PerspectiveRow{TModel}.Data"/> property.
  /// This contains the TModel with all business data fields.
  /// </summary>
  Data = 1 << 0,

  /// <summary>
  /// Expose the <see cref="Whizbang.Core.Lenses.PerspectiveRow{TModel}.Metadata"/> property.
  /// Contains EventType, EventId, Timestamp, CorrelationId, CausationId.
  /// Useful for audit trails and event tracing.
  /// </summary>
  Metadata = 1 << 1,

  /// <summary>
  /// Expose the <see cref="Whizbang.Core.Lenses.PerspectiveRow{TModel}.Scope"/> property.
  /// Contains TenantId, UserId, OrganizationId, CustomerId, AllowedPrincipals.
  /// Useful for multi-tenancy and access control visibility.
  /// </summary>
  Scope = 1 << 2,

  /// <summary>
  /// Expose system fields: Id, CreatedAt, UpdatedAt, Version.
  /// Useful for optimistic concurrency and audit.
  /// </summary>
  SystemFields = 1 << 3,

  /// <summary>
  /// Preset: Expose only the model data. Equivalent to <see cref="Data"/>.
  /// This is the simplest and most common configuration.
  /// </summary>
  DataOnly = Data,

  /// <summary>
  /// Preset: Expose everything except data.
  /// Useful for metadata-only queries (e.g., "when was this last updated?").
  /// </summary>
  NoData = Metadata | Scope | SystemFields,

  /// <summary>
  /// Preset: Expose all parts of the PerspectiveRow.
  /// Best for admin interfaces or audit dashboards.
  /// </summary>
  All = Data | Metadata | Scope | SystemFields
}
