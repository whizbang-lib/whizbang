// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using HotChocolate.Execution.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Transports.HotChocolate;

/// <summary>
/// Production-hardening defaults for HotChocolate GraphQL services.
/// </summary>
/// <docs>apis/graphql/production-hardening</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/HotChocolateSecurityExtensionsTests.cs</tests>
/// <example>
/// services.AddGraphQLServer()
///     .AddWhizbangLenses()
///     .AddWhizbangGraphQLSecurityDefaults(isProduction: !builder.Environment.IsDevelopment());
/// </example>
public static class HotChocolateSecurityExtensions {
  /// <summary>
  /// The deepest query <see cref="AddWhizbangGraphQLSecurityDefaults"/> accepts unless the host sets
  /// another limit. A lens query with paging and one level of nested objects stays well under it.
  /// </summary>
  public const int DEFAULT_MAX_EXECUTION_DEPTH = 15;

  /// <summary>
  /// Limits how deep a query may nest, in every environment, and when <paramref name="isProduction"/> is
  /// <c>true</c> also disables schema introspection and strips exception details (stack traces, exception
  /// messages) from GraphQL errors. Takes an explicit flag rather than sniffing the host environment
  /// because "production" here usually means <i>every deployed environment</i>, which only the host
  /// application can decide; pass <c>!builder.Environment.IsDevelopment()</c> for the common case.
  /// </summary>
  /// <remarks>
  /// HotChocolate already enforces cost limits and a 30 second execution timeout by default; nothing
  /// bounds depth, and each level multiplies resolver work. The depth limit is not tied to
  /// <paramref name="isProduction"/> so a query development accepts is never one production refuses.
  /// Introspection fields are not counted against it, so development tooling keeps working.
  /// </remarks>
  /// <param name="builder">The HotChocolate request executor builder.</param>
  /// <param name="isProduction">Whether introspection and exception details should be turned off.</param>
  /// <param name="maxExecutionDepth">The deepest query accepted; <see cref="DEFAULT_MAX_EXECUTION_DEPTH"/> by default.</param>
  /// <returns>The builder for chaining.</returns>
  public static IRequestExecutorBuilder AddWhizbangGraphQLSecurityDefaults(
      this IRequestExecutorBuilder builder,
      bool isProduction,
      int maxExecutionDepth = DEFAULT_MAX_EXECUTION_DEPTH) {
    builder = builder.AddMaxExecutionDepthRule(maxExecutionDepth, skipIntrospectionFields: true);
    if (!isProduction) {
      return builder;
    }

    return builder
        .DisableIntrospection()
        .ModifyRequestOptions(options => options.IncludeExceptionDetails = false);
  }
}
