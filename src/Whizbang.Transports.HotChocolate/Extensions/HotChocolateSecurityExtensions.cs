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
  /// When <paramref name="isProduction"/> is <c>true</c>, disables schema introspection and strips
  /// exception details (stack traces, exception messages) from GraphQL errors. When <c>false</c>, this is
  /// a no-op so local development keeps introspection tooling and full error detail. Takes an explicit
  /// flag rather than sniffing the host environment because "production" here usually means <i>every
  /// deployed environment</i>, which only the host application can decide — pass
  /// <c>!builder.Environment.IsDevelopment()</c> for the common case.
  /// </summary>
  /// <param name="builder">The HotChocolate request executor builder.</param>
  /// <param name="isProduction">Whether production hardening should be applied.</param>
  /// <returns>The builder for chaining.</returns>
  public static IRequestExecutorBuilder AddWhizbangGraphQLSecurityDefaults(
      this IRequestExecutorBuilder builder,
      bool isProduction) {
    if (!isProduction) {
      return builder;
    }

    return builder
        .DisableIntrospection()
        .ModifyRequestOptions(options => options.IncludeExceptionDetails = false);
  }
}
