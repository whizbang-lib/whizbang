// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate.Data.Filters;
using Whizbang.Core.Lenses;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// The GraphQL filter input type for one <see cref="GraphQLLensAttribute"/> lens. It offers only the parts of
/// <see cref="PerspectiveRow{TModel}"/> the lens exposes (see <see cref="GraphQLLensScopeResolver.ResolveForInput"/>),
/// so a filter cannot test a field the lens does not return.
/// </summary>
/// <typeparam name="TModel">The lens's read model.</typeparam>
/// <docs>apis/graphql/filtering</docs>
/// <tests>tests/Whizbang.Generators.Tests/GraphQLLensScopeSchemaTests.cs</tests>
/// <remarks>
/// Creates the filter input type for one lens.
/// </remarks>
/// <param name="typeName">The GraphQL type name.</param>
/// <param name="declaredScope">The lens's <see cref="GraphQLLensAttribute.Scope"/>.</param>
public abstract class LensPerspectiveRowFilterInputType<TModel>(string typeName, GraphQLLensScopes declaredScope) : FilterInputType<PerspectiveRow<TModel>> where TModel : class {
  private readonly string _typeName = typeName;
  private readonly GraphQLLensScopes _declaredScope = declaredScope;

  /// <inheritdoc/>
  protected override void Configure(IFilterInputTypeDescriptor<PerspectiveRow<TModel>> descriptor) {
    descriptor.Name(_typeName);
    descriptor.BindFieldsExplicitly();

    var options = GraphQLLensScopeResolver.GetOptions(descriptor.Extend().Context);
    var scope = GraphQLLensScopeResolver.ResolveForInput(_declaredScope, options);
    if (scope.HasFlag(GraphQLLensScopes.Data)) {
      descriptor.Field(row => row.Data);
    }
    if (scope.HasFlag(GraphQLLensScopes.Metadata)) {
      descriptor.Field(row => row.Metadata);
    }
    if (scope.HasFlag(GraphQLLensScopes.Scope)) {
      descriptor.Field(row => row.Scope);
    }
    if (scope.HasFlag(GraphQLLensScopes.SystemFields)) {
      descriptor.Field(row => row.Id);
      descriptor.Field(row => row.CreatedAt);
      descriptor.Field(row => row.UpdatedAt);
      descriptor.Field(row => row.Version);
    }
  }
}
