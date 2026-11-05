// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Transports.HotChocolate.Types;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Extension methods that let a <c>[WhizbangId]</c> type be a GraphQL argument, input field or output field.
/// </summary>
/// <docs>apis/graphql/setup</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/WhizbangIdGraphQLTests.cs</tests>
/// <example>
/// services.AddGraphQLServer()
///     .AddWhizbangLenses()
///     .AddWhizbangIds()
///     .AddQueryType&lt;Query&gt;();
///
/// // A resolver can then take the id itself:
/// public Order GetOrder(OrderId id) => ...;
/// </example>
public static class HotChocolateWhizbangIdExtensions {
  /// <summary>
  /// Adds a scalar for every generated <c>[WhizbangId]</c> type, named after the type and written as a UUID string,
  /// so a resolver can take the id as an argument or an input field and return it. Input goes through the id's
  /// own <c>From(Guid)</c>: a Guid the id refuses (not UUIDv7, or empty for an id declared with
  /// <c>AcceptAnyVersion</c>) is a GraphQL error with the code <c>WHIZBANG_INVALID_ID</c>, not an exception in
  /// the resolver.
  /// </summary>
  /// <remarks>
  /// The ids are read from <see cref="WhizbangIdProviderRegistry"/> when the schema is built. Each assembly's
  /// generated module initializer fills it, which runs before any code in that assembly does, as it does for
  /// the id providers and JSON converters.
  /// </remarks>
  /// <param name="builder">The HotChocolate request executor builder.</param>
  /// <returns>The builder for chaining.</returns>
  public static IRequestExecutorBuilder AddWhizbangIds(this IRequestExecutorBuilder builder) =>
    builder.ConfigureSchema(schema => {
      foreach (var (idType, fromGuid) in WhizbangIdProviderRegistry.GetGuidConversions()) {
        schema.AddType(new WhizbangIdType(idType, fromGuid));
      }
    });
}
