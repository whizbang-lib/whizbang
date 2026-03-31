using Microsoft.AspNetCore.Builder;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Extension methods for registering <see cref="DatabaseAvailabilityMiddleware"/>.
/// </summary>
public static class DatabaseAvailabilityMiddlewareExtensions {
  /// <summary>
  /// Adds middleware that returns 503 when the database is unavailable.
  /// Place early in the pipeline (before routing) to reject requests fast.
  /// </summary>
  /// <docs>resilience/database-availability-middleware</docs>
  public static IApplicationBuilder UseDatabaseAvailabilityGate(this IApplicationBuilder app) {
    return app.UseMiddleware<DatabaseAvailabilityMiddleware>();
  }
}
