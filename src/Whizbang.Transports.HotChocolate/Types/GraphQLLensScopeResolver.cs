// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate.Types.Descriptors;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Decides which parts of a <see cref="Whizbang.Core.Lenses.PerspectiveRow{TModel}"/> a
/// <see cref="GraphQLLensAttribute"/> lens exposes, from the lens's declared
/// <see cref="GraphQLLensAttribute.Scope"/> and the system-wide <see cref="WhizbangGraphQLOptions.DefaultScope"/>.
/// </summary>
/// <remarks>
/// <para>The resolution fails closed: a scope that resolves to <see cref="GraphQLLensScopes.None"/>, or that
/// carries a bit outside <see cref="GraphQLLensScopes.All"/>, exposes <see cref="GraphQLLensScopes.Data"/>
/// only. An unrecognized value never widens what a lens exposes.</para>
/// </remarks>
/// <docs>apis/graphql/lens-integration#scope</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/GraphQLLensScopeResolverTests.cs</tests>
public static class GraphQLLensScopeResolver {
  /// <summary>
  /// The schema context-data key under which <see cref="HotChocolateWhizbangExtensions.AddWhizbangLenses(global::HotChocolate.Execution.Configuration.IRequestExecutorBuilder, Action{WhizbangGraphQLOptions})"/>
  /// publishes its <see cref="WhizbangGraphQLOptions"/> to the generated lens types.
  /// </summary>
  public const string OPTIONS_CONTEXT_KEY = "Whizbang.Transports.HotChocolate.WhizbangGraphQLOptions";

  /// <summary>
  /// Resolves the scope a lens exposes.
  /// </summary>
  /// <param name="declaredScope">The lens's <see cref="GraphQLLensAttribute.Scope"/>.</param>
  /// <param name="defaultScope">The <see cref="WhizbangGraphQLOptions.DefaultScope"/> applied when
  /// <paramref name="declaredScope"/> is <see cref="GraphQLLensScopes.None"/>.</param>
  /// <returns>The parts of the row the lens exposes; never <see cref="GraphQLLensScopes.None"/>.</returns>
  public static GraphQLLensScopes Resolve(GraphQLLensScopes declaredScope, GraphQLLensScopes defaultScope) {
    var effective = declaredScope == GraphQLLensScopes.None ? defaultScope : declaredScope;
    return effective == GraphQLLensScopes.None || (effective & ~GraphQLLensScopes.All) != 0
        ? GraphQLLensScopes.Data
        : effective;
  }

  /// <summary>
  /// Resolves the scope a lens's generated row type exposes, reading <see cref="WhizbangGraphQLOptions.DefaultScope"/>
  /// from the schema being built. Called by the code the lens generator emits.
  /// </summary>
  /// <param name="declaredScope">The lens's <see cref="GraphQLLensAttribute.Scope"/>.</param>
  /// <param name="context">The descriptor context of the type being configured.</param>
  /// <returns>The parts of the row the lens exposes.</returns>
  public static GraphQLLensScopes ResolveForOutput(GraphQLLensScopes declaredScope, IDescriptorContext context) {
    ArgumentNullException.ThrowIfNull(context);
    return Resolve(declaredScope, GetOptions(context).DefaultScope);
  }

  /// <summary>
  /// Resolves the scope a lens's filter and sort input types expose: the lens's own scope, further narrowed by
  /// <see cref="WhizbangGraphQLOptions.IncludeMetadataInFilters"/> and <see cref="WhizbangGraphQLOptions.IncludeScopeInFilters"/>.
  /// An input type never offers a part the lens does not expose.
  /// </summary>
  /// <param name="declaredScope">The lens's <see cref="GraphQLLensAttribute.Scope"/>.</param>
  /// <param name="options">The system-wide GraphQL options.</param>
  /// <returns>The parts of the row the lens's filter and sort input types expose.</returns>
  public static GraphQLLensScopes ResolveForInput(GraphQLLensScopes declaredScope, WhizbangGraphQLOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var scope = Resolve(declaredScope, options.DefaultScope);
    if (!options.IncludeMetadataInFilters) {
      scope &= ~GraphQLLensScopes.Metadata;
    }
    if (!options.IncludeScopeInFilters) {
      scope &= ~GraphQLLensScopes.Scope;
    }
    return scope;
  }

  /// <summary>
  /// Resolves the scope a lens's generated filter or sort input type exposes, reading the options from the schema
  /// being built. Called by the code the lens generator emits.
  /// </summary>
  /// <param name="declaredScope">The lens's <see cref="GraphQLLensAttribute.Scope"/>.</param>
  /// <param name="context">The descriptor context of the type being configured.</param>
  /// <returns>The parts of the row the lens's input types expose.</returns>
  public static GraphQLLensScopes ResolveForInput(GraphQLLensScopes declaredScope, IDescriptorContext context) {
    ArgumentNullException.ThrowIfNull(context);
    return ResolveForInput(declaredScope, GetOptions(context));
  }

  /// <summary>
  /// Reads the options <see cref="HotChocolateWhizbangExtensions.AddWhizbangLenses(global::HotChocolate.Execution.Configuration.IRequestExecutorBuilder, Action{WhizbangGraphQLOptions})"/>
  /// published to the schema, or the defaults when none were published.
  /// </summary>
  internal static WhizbangGraphQLOptions GetOptions(IDescriptorContext context) =>
      context.ContextData.TryGetValue(OPTIONS_CONTEXT_KEY, out var value) && value is WhizbangGraphQLOptions options
          ? options
          : new WhizbangGraphQLOptions();
}
