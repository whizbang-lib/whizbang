using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Workers;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Extension methods for registering <see cref="DatabaseAvailabilityMiddleware"/>.
/// </summary>
public static class DatabaseAvailabilityMiddlewareExtensions {
  /// <summary>
  /// Adds the schema-availability gate: returns 503 for non-exempt requests until migrations complete,
  /// then passes through. Uses <see cref="DatabaseAvailabilityMiddleware.DefaultExemptPaths"/> so the
  /// standard liveness/readiness/version probes keep working during initialization. Place early in the
  /// pipeline (before routing) to reject requests fast.
  /// </summary>
  /// <docs>resilience/database-availability-middleware</docs>
  /// <tests>tests/Whizbang.Hosting.AspNet.Tests/DatabaseAvailabilityMiddlewareExtensionsTests.cs:UseDatabaseAvailabilityGate_RegistersMiddleware_AndReturnsBuilderAsync</tests>
  public static IApplicationBuilder UseDatabaseAvailabilityGate(this IApplicationBuilder app)
    => app.UseDatabaseAvailabilityGate(exemptPaths: null);

  /// <summary>
  /// Adds the schema-availability gate with an explicit set of exempt path prefixes (e.g. custom
  /// health/probe endpoints) that always pass through even before the schema is ready
  /// (<see langword="null"/> uses <see cref="DatabaseAvailabilityMiddleware.DefaultExemptPaths"/>), and
  /// a <paramref name="mode"/> selecting whether to gate every non-exempt request or only mutating
  /// ones (<see cref="AvailabilityGateMode.MutationsOnly"/> lets reads serve during migration).
  /// </summary>
  /// <docs>resilience/database-availability-middleware</docs>
  /// <tests>tests/Whizbang.Hosting.AspNet.Tests/DatabaseAvailabilityMiddlewareExtensionsTests.cs:UseDatabaseAvailabilityGate_WithExemptPaths_ReturnsBuilderAsync</tests>
  public static IApplicationBuilder UseDatabaseAvailabilityGate(
      this IApplicationBuilder app, IReadOnlyList<string>? exemptPaths,
      AvailabilityGateMode mode = AvailabilityGateMode.AllNonExempt) {
    ArgumentNullException.ThrowIfNull(app);
    // Resolve the singleton gate once at build time and construct the middleware explicitly, so the
    // gate (DI) and the exempt paths (explicit) are unambiguously combined.
    var gate = app.ApplicationServices.GetRequiredService<ISchemaReadyGate>();
    return app.Use(next => {
      var middleware = new DatabaseAvailabilityMiddleware(next, gate, exemptPaths, mode);
      return middleware.InvokeAsync;
    });
  }
}
