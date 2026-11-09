// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Transports.HotChocolate.Types;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Extension methods that let a <c>[WhizbangId]</c> type be a GraphQL argument, input field or output field.
/// </summary>
/// <docs>apis/graphql/setup</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Ids.Tests/Unit/WhizbangIdGraphQLTests.cs</tests>
/// <tests>tests/Whizbang.Transports.HotChocolate.Ids.Tests/Unit/WhizbangIdSchemaRegistrationTests.cs</tests>
/// <example>
/// // AddWhizbangLenses() adds the id scalars itself:
/// services.AddGraphQLServer()
///     .AddWhizbangLenses()
///     .AddQueryType&lt;Query&gt;();
///
/// // An executor without lenses adds them on its own:
/// services.AddGraphQLServer()
///     .AddWhizbangIds()
///     .AddQueryType&lt;Query&gt;();
///
/// // A resolver can then take the id itself:
/// public Order GetOrder(OrderId id) => ...;
/// </example>
public static class HotChocolateWhizbangIdExtensions {
  /// <summary>
  /// Gives every generated <c>[WhizbangId]</c> type the schema uses a scalar, named after the type and written as a
  /// UUID string, so a resolver can take the id as an argument or an input field and return it. Input goes through
  /// the id's own <c>From(Guid)</c>: a Guid the id refuses (not UUIDv7, or empty for an id declared with
  /// <c>AcceptAnyVersion</c>) is a GraphQL error with the code <c>WHIZBANG_INVALID_ID</c>, not an exception in the
  /// resolver.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <c>AddWhizbangLenses()</c> calls this, so only an executor without lenses needs it. Calling it more than once
  /// for the same schema, or together with <c>AddWhizbangLenses()</c>, adds the scalars once.
  /// </para>
  /// <para>
  /// A scalar is added only for an id the schema refers to, so the framework's own ids (<c>StreamId</c>,
  /// <c>EventId</c>, <c>MessageId</c>, <c>CorrelationId</c>) appear only in a schema that uses them, and a consumer
  /// id with one of those names gets its own scalar. Each id's conversion is read when the schema is built, after
  /// the generated module initializer of the id's assembly has been run, so no code in that assembly needs to have
  /// run first.
  /// </para>
  /// </remarks>
  /// <param name="builder">The HotChocolate request executor builder.</param>
  /// <returns>The builder for chaining.</returns>
  public static IRequestExecutorBuilder AddWhizbangIds(this IRequestExecutorBuilder builder) {
    if (builder.Services.Any(service => service.ServiceType == typeof(WhizbangIdsAdded)
        && ((WhizbangIdsAdded)service.ImplementationInstance!).SchemaName == builder.Name)) {
      return builder;
    }

    builder.Services.AddSingleton(new WhizbangIdsAdded(builder.Name));
    return builder.AddTypeDiscoveryHandler(_ => new WhizbangIdTypeDiscoveryHandler());
  }

  /// <summary>Marks a schema whose builder has had <see cref="AddWhizbangIds"/>, so a second call adds nothing.</summary>
  /// <param name="SchemaName">The name of the schema.</param>
  private sealed record WhizbangIdsAdded(string SchemaName);
}
