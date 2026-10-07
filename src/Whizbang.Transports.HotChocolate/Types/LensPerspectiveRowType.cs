// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate.Types;
using Whizbang.Core.Lenses;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// The GraphQL object type for the rows of one <see cref="GraphQLLensAttribute"/> lens. It binds only the parts
/// of <see cref="PerspectiveRow{TModel}"/> the lens's scope exposes (see <see cref="GraphQLLensScopeResolver"/>).
/// The lens generator derives one sealed type per lens, so two lenses over the same model keep their own field sets.
/// </summary>
/// <typeparam name="TModel">The lens's read model.</typeparam>
/// <docs>apis/graphql/lens-integration#scope</docs>
/// <tests>tests/Whizbang.Generators.Tests/GraphQLLensScopeSchemaTests.cs</tests>
/// <remarks>
/// Creates the row type for one lens.
/// </remarks>
/// <param name="typeName">The GraphQL type name.</param>
/// <param name="declaredScope">The lens's <see cref="GraphQLLensAttribute.Scope"/>.</param>
public abstract class LensPerspectiveRowType<TModel>(string typeName, GraphQLLensScopes declaredScope) : ObjectType<PerspectiveRow<TModel>> where TModel : class {
  private readonly string _typeName = typeName;
  private readonly GraphQLLensScopes _declaredScope = declaredScope;

  /// <inheritdoc/>
  protected override void Configure(IObjectTypeDescriptor<PerspectiveRow<TModel>> descriptor) {
    descriptor.Name(_typeName);
    descriptor.BindFieldsExplicitly();

    var options = GraphQLLensScopeResolver.GetOptions(descriptor.Extend().Context);
    var scope = GraphQLLensScopeResolver.Resolve(_declaredScope, options.DefaultScope);
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
