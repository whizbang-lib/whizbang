// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using OpenTelemetry.Trace;
using Whizbang.Core.Observability;

namespace Whizbang.Observability.DependencyInjection;

/// <summary>
/// Subscribes an OpenTelemetry tracing pipeline to every activity source Whizbang publishes (#2365).
/// </summary>
/// <docs>operations/observability/tracing#collecting-activities</docs>
/// <tests>tests/Whizbang.Observability.Tests/DependencyInjection/WhizbangTracingRegistrationTests.cs</tests>
public static class WhizbangTracingRegistration {
  /// <summary>Subscribes the tracing pipeline to every source in <see cref="WhizbangActivitySource.All"/>.</summary>
  /// <param name="builder">The tracer builder to configure.</param>
  /// <returns>The same builder, for chaining.</returns>
  public static TracerProviderBuilder AddWhizbangInstrumentation(this TracerProviderBuilder builder) {
    ArgumentNullException.ThrowIfNull(builder);
    return builder.AddSource([.. WhizbangActivitySource.All]);
  }
}
